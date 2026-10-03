using System.Buffers.Binary;
using SmartPark.Api.Common;
using SmartPark.Api.Data;

namespace SmartPark.Api.Storage;

public sealed class LocalFileStore(IConfiguration configuration, ParkDbContext db)
{
    private readonly string _root = Path.GetFullPath(configuration["Storage:Root"] ?? "/uploads");

    public async Task<StoredFile> SaveAsync(IFormFile file, string kind, Guid? ownerId, bool isPublic, bool temporary, CancellationToken ct)
    {
        await FileValidation.ValidateAsync(file, kind, ct);
        Directory.CreateDirectory(_root);
        var extension = FileValidation.GetExtension(file.FileName);
        var name = $"{Guid.NewGuid():N}{extension}";
        var path = Path.Combine(_root, name);
        var created = false;
        try
        {
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                created = true;
                await file.CopyToAsync(output, ct);
            }
            var stored = new StoredFile
            {
                StorageName = name,
                OriginalName = FileValidation.OriginalName(file.FileName),
                ContentType = FileValidation.ContentTypeForExtension(extension),
                Length = file.Length,
                Kind = kind,
                OwnerId = ownerId,
                IsPublic = isPublic,
                IsTemporary = temporary
            };
            db.StoredFiles.Add(stored);
            await db.SaveChangesAsync(ct);
            return stored;
        }
        catch
        {
            if (created)
            {
                try { System.IO.File.Delete(path); }
                catch { }
            }
            throw;
        }
    }

    public string GetPath(StoredFile file)
    {
        if (!string.Equals(Path.GetFileName(file.StorageName), file.StorageName, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(file.StorageName)) throw new ApiException("Invalid stored file", 500);
        var path = Path.GetFullPath(Path.Combine(_root, file.StorageName));
        var relative = Path.GetRelativePath(_root, path);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative)) throw new ApiException("Invalid stored file", 500);
        return path;
    }
}

public static class FileValidation
{
    private const long PhotoLimit = 5L * 1024 * 1024;
    private const long DomLimit = 20L * 1024 * 1024;
    private const long ImportLimit = 10L * 1024 * 1024;
    private const long AudioLimit = 10L * 1024 * 1024;
    private const int MaxImageDimension = 12000;
    private const long MaxDomPixels = 40_000_000;
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

    public static string OriginalName(string fileName) => Path.GetFileName((fileName ?? "").Replace('\\', '/'));
    public static string GetExtension(string fileName) => Path.GetExtension(OriginalName(fileName)).ToLowerInvariant();

    public static string ContentTypeForExtension(string extension) => extension.ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".wav" => "audio/wav",
        ".csv" => "text/csv",
        ".geojson" => "application/geo+json",
        ".json" => "application/json",
        _ => "application/octet-stream"
    };

    public static async Task ValidateAsync(IFormFile file, string kind, CancellationToken ct)
    {
        var extension = GetExtension(file.FileName);
        await using var input = file.OpenReadStream();
        await ValidateStreamAsync(input, file.Length, extension, kind, ct);
    }

    public static async Task ValidateStoredAsync(string path, string originalName, string kind, CancellationToken ct)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new ApiException("Invalid file", 400, "The stored file is unavailable.");
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await ValidateStreamAsync(input, info.Length, GetExtension(originalName), kind, ct);
    }

    private static async Task ValidateStreamAsync(Stream input, long length, string extension, string kind, CancellationToken ct)
    {
        if (length <= 0) throw new ApiException("Invalid file", 400, "The uploaded file is empty.");
        var limit = LimitFor(kind);
        if (length > limit) throw new ApiException("File too large", 400, $"Maximum size is {limit} bytes.");
        if (!AllowedExtensions(kind).Contains(extension, StringComparer.OrdinalIgnoreCase)) throw new ApiException("Unsupported file", 400, "The file extension is not allowed.");

        if (extension == ".png")
        {
            var prefix = await ReadPrefixAsync(input, 24, ct);
            var dimensions = ValidatePng(prefix);
            if (kind == "dom") ValidateDomDimensions(dimensions);
        }
        else if (extension is ".jpg" or ".jpeg")
        {
            if (input.CanSeek) input.Position = 0;
            var dimensions = await ReadJpegDimensionsAsync(input, ct);
            if (kind == "dom") ValidateDomDimensions(dimensions);
        }
        else if (extension == ".webp")
        {
            var prefix = await ReadPrefixAsync(input, 12, ct);
            if (prefix.Length < 12 || !prefix.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !prefix.AsSpan(8, 4).SequenceEqual("WEBP"u8)) throw new ApiException("Invalid file", 400, "The WebP signature is invalid.");
        }
        else if (extension == ".wav")
        {
            var prefix = await ReadPrefixAsync(input, 12, ct);
            if (prefix.Length < 12 || !prefix.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !prefix.AsSpan(8, 4).SequenceEqual("WAVE"u8)) throw new ApiException("Invalid file", 400, "The WAV signature is invalid.");
        }
    }

    private static long LimitFor(string kind) => kind switch
    {
        "photo" => PhotoLimit,
        "dom" => DomLimit,
        "import" => ImportLimit,
        "audio" => AudioLimit,
        _ => PhotoLimit
    };

    private static string[] AllowedExtensions(string kind) => kind switch
    {
        "photo" => [".png", ".jpg", ".jpeg", ".webp"],
        "dom" => [".png", ".jpg", ".jpeg"],
        "import" => [".csv", ".geojson", ".json"],
        "audio" => [".wav"],
        _ => []
    };

    private static async Task<byte[]> ReadPrefixAsync(Stream input, int maximum, CancellationToken ct)
    {
        if (input.CanSeek) input.Position = 0;
        var buffer = new byte[maximum];
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await input.ReadAsync(buffer.AsMemory(read, buffer.Length - read), ct);
            if (count == 0) break;
            read += count;
        }
        return read == buffer.Length ? buffer : buffer[..read];
    }

    private static ImageDimensions ValidatePng(byte[] prefix)
    {
        if (prefix.Length < 24 || !prefix.AsSpan(0, 8).SequenceEqual(PngSignature) || BinaryPrimitives.ReadUInt32BigEndian(prefix.AsSpan(8, 4)) != 13 || !prefix.AsSpan(12, 4).SequenceEqual("IHDR"u8)) throw new ApiException("Invalid file", 400, "The PNG header is malformed.");
        var width = BinaryPrimitives.ReadUInt32BigEndian(prefix.AsSpan(16, 4));
        var height = BinaryPrimitives.ReadUInt32BigEndian(prefix.AsSpan(20, 4));
        if (width == 0 || height == 0 || width > int.MaxValue || height > int.MaxValue) throw new ApiException("Invalid file", 400, "The PNG dimensions are invalid.");
        return new ImageDimensions((int)width, (int)height);
    }

    private static async Task<ImageDimensions> ReadJpegDimensionsAsync(Stream input, CancellationToken ct)
    {
        const int maximumHeaderBytes = 1024 * 1024;
        var one = new byte[1];
        var consumed = 0;

        async ValueTask<int> ReadByteAsync()
        {
            if (consumed >= maximumHeaderBytes) return -1;
            var count = await input.ReadAsync(one.AsMemory(0, 1), ct);
            if (count == 0) return -1;
            consumed++;
            return one[0];
        }

        async ValueTask<bool> SkipAsync(int count)
        {
            while (count > 0)
            {
                if (await ReadByteAsync() < 0) return false;
                count--;
            }
            return true;
        }

        if (await ReadByteAsync() != 0xff || await ReadByteAsync() != 0xd8) throw new ApiException("Invalid file", 400, "The JPEG signature is invalid.");
        while (true)
        {
            var markerPrefix = await ReadByteAsync();
            while (markerPrefix != 0xff)
            {
                if (markerPrefix < 0) throw new ApiException("Invalid file", 400, "The JPEG header is malformed.");
                markerPrefix = await ReadByteAsync();
            }
            var marker = await ReadByteAsync();
            while (marker == 0xff) marker = await ReadByteAsync();
            if (marker < 0 || marker == 0x00 || marker == 0xd9) throw new ApiException("Invalid file", 400, "The JPEG header is malformed.");
            if (marker is 0xd8 or 0x01 || marker is >= 0xd0 and <= 0xd7) continue;

            var lengthHigh = await ReadByteAsync();
            var lengthLow = await ReadByteAsync();
            if (lengthHigh < 0 || lengthLow < 0) throw new ApiException("Invalid file", 400, "The JPEG header is malformed.");
            var segmentLength = (lengthHigh << 8) | lengthLow;
            if (segmentLength < 2) throw new ApiException("Invalid file", 400, "The JPEG segment length is invalid.");

            if (IsSofMarker(marker))
            {
                if (segmentLength < 8) throw new ApiException("Invalid file", 400, "The JPEG dimensions are invalid.");
                if (await ReadByteAsync() < 0) throw new ApiException("Invalid file", 400, "The JPEG header is malformed.");
                var heightHigh = await ReadByteAsync();
                var heightLow = await ReadByteAsync();
                var widthHigh = await ReadByteAsync();
                var widthLow = await ReadByteAsync();
                if (heightHigh < 0 || heightLow < 0 || widthHigh < 0 || widthLow < 0) throw new ApiException("Invalid file", 400, "The JPEG header is malformed.");
                var height = (heightHigh << 8) | heightLow;
                var width = (widthHigh << 8) | widthLow;
                if (width == 0 || height == 0) throw new ApiException("Invalid file", 400, "The JPEG dimensions are invalid.");
                return new ImageDimensions(width, height);
            }

            if (!await SkipAsync(segmentLength - 2)) throw new ApiException("Invalid file", 400, "The JPEG header is malformed.");
        }
    }

    private static bool IsSofMarker(int marker) => marker is >= 0xc0 and <= 0xc3 or >= 0xc5 and <= 0xc7 or >= 0xc9 and <= 0xcb or >= 0xcd and <= 0xcf;

    private static void ValidateDomDimensions(ImageDimensions dimensions)
    {
        if (dimensions.Width > MaxImageDimension || dimensions.Height > MaxImageDimension || (long)dimensions.Width * dimensions.Height > MaxDomPixels) throw new ApiException("Invalid file", 400, $"DOM dimensions must not exceed {MaxImageDimension} pixels per side or {MaxDomPixels} pixels total.");
    }

    private readonly record struct ImageDimensions(int Width, int Height);
}
