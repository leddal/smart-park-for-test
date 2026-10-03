using System.Data;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartPark.Api.Common;
using SmartPark.Api.Data;
using SmartPark.Api.Storage;

namespace SmartPark.Api.Features.DataPlatform;

[ApiController]
[Route("api/platform")]
[Authorize(Policy = Policies.Manager)]
public sealed class DataPlatformController(ParkDbContext db, LocalFileStore files, AuditService audit) : ControllerBase
{
    private static readonly HashSet<string> CsvHeaders = new(StringComparer.Ordinal)
    {
        "code", "name", "category", "zone", "zoneId", "zoneName", "zoneCode", "longitude", "latitude", "publicDescription",
        "species", "diameterCm", "heightM", "kind", "deviceType", "deviceCode"
    };

    [HttpGet("park")]
    public async Task<object> Park(CancellationToken ct)
    {
        var park = await db.Parks.AsNoTracking().Include(x => x.Zones).SingleOrDefaultAsync(ct) ?? throw new ApiException("Park not initialized", 404);
        return new { park.Id, park.Name, park.Description, park.OpenHours, park.Phone, park.Longitude, park.Latitude, zones = park.Zones.Select(z => new { z.Id, z.Name, z.Code, z.Description }) };
    }

    [Authorize(Policy = Policies.Administrator), HttpPut("park")]
    public async Task<object> UpdatePark(ParkRequest request, CancellationToken ct)
    {
        var park = await db.Parks.Include(x => x.Zones).SingleAsync(ct);
        if (request.Longitude is < -180 or > 180 || request.Latitude is < -90 or > 90) throw new ApiException("Validation failed", 400, "Coordinates are invalid.");
        park.Name = request.Name.Trim(); park.Description = request.Description ?? ""; park.OpenHours = request.OpenHours ?? ""; park.Phone = request.Phone ?? ""; park.Longitude = request.Longitude; park.Latitude = request.Latitude;
        await db.SaveChangesAsync(ct); await audit.WriteAsync("Update", "Park", park.Id, null, ct); return await Park(ct);
    }

    [HttpGet("imports")]
    public async Task<PageResult<object>> Imports(int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        var query = from batch in db.ImportBatches.AsNoTracking()
                    join stored in db.StoredFiles.AsNoTracking() on batch.FileId equals (Guid?)stored.Id into storedFiles
                    from stored in storedFiles.DefaultIfEmpty()
                    orderby batch.CreatedAt descending
                    select new
                    {
                        batch.Id, batch.Type, batch.LayerKind, batch.Status, batch.Rows, batch.ErrorsJson, batch.CreatedAt, batch.OperatorId,
                        originalFileName = stored == null ? null : stored.OriginalName,
                        metadata = new { fileKind = batch.Type, originalFileName = stored == null ? null : stored.OriginalName, batch.OperatorId, batch.CreatedAt, errors = batch.ErrorsJson }
                    };
        var data = await query.ToPageAsync(page, pageSize, ct);
        return new PageResult<object>(data.Items.Cast<object>().ToList(), data.Total, data.Page, data.PageSize);
    }

    [Authorize(Policy = Policies.Administrator), HttpPost("imports/preview")]
    [RequestSizeLimit(21 * 1024 * 1024)]
    public async Task<object> Preview([FromForm] ImportPreviewForm form, CancellationToken ct)
    {
        if (form.File is null || form.File.Length == 0) throw new ApiException("Validation failed", 400, "A file is required.");
        if (form.Type is not ("GeoJSON" or "CSV" or "DOM") || form.LayerKind is not ("Boundary" or "Road" or "POI")) throw new ApiException("Validation failed", 400, "Type or layer kind is invalid.");

        var kind = form.Type == "DOM" ? "dom" : "import";
        var stored = await files.SaveAsync(form.File, kind, User.Id(), false, false, ct);
        var validation = await ValidateImportAsync(form.Type, form.LayerKind, stored, form.Bounds, ct);
        var previewedAt = DateTimeOffset.UtcNow;
        var batch = new ImportBatch
        {
            Type = form.Type,
            LayerKind = form.LayerKind,
            FileId = stored.Id,
            OperatorId = User.Id(),
            Rows = validation.Rows,
            ErrorsJson = JsonSerializer.Serialize(validation.Errors),
            PayloadJson = BuildPayloadMetadata(form.Type, stored.OriginalName, User.Id(), previewedAt, validation.BoundsJson),
            Status = validation.Errors.Count == 0 ? "Preview" : "Invalid"
        };
        db.ImportBatches.Add(batch);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("Preview", "Import", batch.Id, new { batch.Type, batch.LayerKind }, ct);
        return new
        {
            batch.Id,
            valid = validation.Errors.Count == 0,
            rows = validation.Rows,
            errors = validation.Errors,
            metadata = new { fileKind = batch.Type, originalFileName = stored.OriginalName, operatorId = batch.OperatorId, previewedAt = batch.CreatedAt, errors = validation.Errors, inferredExternalIds = validation.InferredExternalIds }
        };
    }

    [Authorize(Policy = Policies.Administrator), HttpPost("imports/{id:guid}/confirm")]
    public async Task<IActionResult> Confirm(Guid id, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var batch = await db.ImportBatches
            .FromSqlInterpolated($"SELECT * FROM \"ImportBatches\" WHERE \"Id\" = {id} FOR UPDATE")
            .SingleOrDefaultAsync(ct) ?? throw new ApiException("Import batch not found", 404);

        if (batch.Status == "Confirmed")
        {
            await transaction.CommitAsync(ct);
            return Ok(new { batch.Id, batch.Status, batch.Rows, idempotent = true });
        }

        if (batch.FileId is null) throw new ApiException("Import file missing", 400);
        var stored = await db.StoredFiles.SingleOrDefaultAsync(x => x.Id == batch.FileId, ct) ?? throw new ApiException("Import file missing", 400);
        var validation = await ValidateImportAsync(batch.Type, batch.LayerKind, stored, ReadBoundsFromPayload(batch.PayloadJson), ct);
        if (validation.Errors.Count > 0)
        {
            batch.Status = "Invalid";
            batch.Rows = validation.Rows;
            batch.ErrorsJson = JsonSerializer.Serialize(validation.Errors);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return BadRequest(new { batch.Id, valid = false, rows = validation.Rows, errors = validation.Errors });
        }

        if (batch.Type == "CSV")
        {
            db.Assets.AddRange(validation.CsvRows!.Select(ToAsset));
        }
        else
        {
            var targetKind = batch.Type == "DOM" ? "DOM" : batch.LayerKind;
            var replacedLayers = await db.MapLayers.Where(x => x.Kind == targetKind).ToListAsync(ct);
            var replacedFileIds = replacedLayers.Where(x => x.FileId is not null).Select(x => x.FileId!.Value).ToArray();
            if (replacedLayers.Count > 0) db.MapLayers.RemoveRange(replacedLayers);
            if (replacedFileIds.Length > 0)
            {
                var replacedFiles = await db.StoredFiles.Where(x => replacedFileIds.Contains(x.Id)).ToListAsync(ct);
                foreach (var replacedFile in replacedFiles) replacedFile.IsPublic = false;
            }

            db.MapLayers.Add(new MapLayer
            {
                Name = Path.GetFileNameWithoutExtension(stored.OriginalName),
                Kind = targetKind,
                GeoJson = batch.Type == "GeoJSON" ? validation.GeoJson : null,
                FileId = batch.Type == "DOM" ? stored.Id : null,
                BoundsJson = batch.Type == "DOM" ? validation.BoundsJson : null,
                IsPublic = true
            });
            if (batch.Type == "DOM") stored.IsPublic = true;
        }

        batch.Status = "Confirmed";
        batch.Rows = validation.Rows;
        batch.ErrorsJson = "[]";
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        await audit.WriteAsync("Confirm", "Import", id, new { batch.Type, batch.Rows }, ct);
        return Ok(new { batch.Id, batch.Status, batch.Rows, valid = true, errors = Array.Empty<string>() });
    }

    [AllowAnonymous, HttpGet("samples/{name}")]
    public IActionResult Sample(string name)
    {
        var approved = new HashSet<string>(StringComparer.Ordinal) { "park.geojson", "assets.csv", "park-dom.png", "broadcast.wav" };
        if (!approved.Contains(name)) throw new ApiException("Sample not found", 404);
        var candidates = new[] { Path.Combine(AppContext.BaseDirectory, "samples", name), Path.Combine(Directory.GetCurrentDirectory(), "samples", name), Path.Combine(Directory.GetParent(Directory.GetCurrentDirectory())?.FullName ?? "", "samples", name) };
        var path = candidates.FirstOrDefault(System.IO.File.Exists) ?? throw new ApiException("Sample not found", 404);
        return PhysicalFile(path, ContentType(name), enableRangeProcessing: true);
    }

    private Asset ToAsset(CsvImportRow row)
    {
        var asset = new Asset
        {
            Code = row.Code,
            Name = row.Name,
            Category = row.Category,
            ZoneId = row.ZoneId,
            Longitude = row.Longitude,
            Latitude = row.Latitude,
            PublicCode = row.PublicCode,
            PublicDescription = row.PublicDescription,
            Status = "Active"
        };
        if (row.Category == "Plant") asset.Plant = new PlantProfile { AssetId = asset.Id, Species = row.Species ?? "", DiameterCm = row.DiameterCm, HeightM = row.HeightM };
        if (row.Category == "Facility") asset.Facility = new FacilityProfile { AssetId = asset.Id, Kind = string.IsNullOrWhiteSpace(row.FacilityKind) ? "Generic" : row.FacilityKind };
        if (row.Category == "Sensor") asset.Device = new Device { AssetId = asset.Id, Code = row.DeviceCode!, Type = row.DeviceType! };
        return asset;
    }

    private async Task<ImportValidation> ValidateImportAsync(string type, string layerKind, StoredFile file, string? bounds, CancellationToken ct)
    {
        var path = files.GetPath(file);
        if (!System.IO.File.Exists(path)) return new ImportValidation(0, ["Stored import file is unavailable."], null, null, null, 0);
        if (!HasCompatibleExtension(type, file.OriginalName)) return new ImportValidation(0, [$"{type} imports do not support the uploaded file extension."], null, null, null, 0);

        try
        {
            await FileValidation.ValidateStoredAsync(path, file.OriginalName, type == "DOM" ? "dom" : "import", ct);
        }
        catch (ApiException ex)
        {
            return new ImportValidation(0, [ex.Message], null, null, null, 0);
        }

        return type switch
        {
            "DOM" => ValidateDomBounds(bounds),
            "GeoJSON" => await ValidateGeoJsonAsync(path, layerKind, ct),
            "CSV" => await ValidateCsvAsync(path, ct),
            _ => new ImportValidation(0, ["Unsupported import type."], null, null, null, 0)
        };
    }

    private static ImportValidation ValidateDomBounds(string? bounds)
    {
        if (string.IsNullOrWhiteSpace(bounds)) return new ImportValidation(0, ["DOM bounds are required."], null, null, null, 0);
        try
        {
            using var doc = JsonDocument.Parse(bounds, new JsonDocumentOptions { MaxDepth = 64 });
            if (!TryNormalizeBounds(doc.RootElement, out var normalized)) return new ImportValidation(0, ["DOM bounds must contain valid southWest and northEast WGS84 longitude/latitude."], null, null, null, 0);
            return new ImportValidation(1, [], null, normalized, null, 0);
        }
        catch (JsonException)
        {
            return new ImportValidation(0, ["DOM bounds are invalid JSON."], null, null, null, 0);
        }
    }

    private async Task<ImportValidation> ValidateGeoJsonAsync(string path, string layerKind, CancellationToken ct)
    {
        var errors = new List<string>();
        string payload;
        try
        {
            payload = await System.IO.File.ReadAllTextAsync(path, ct);
        }
        catch (Exception)
        {
            return new ImportValidation(0, ["GeoJSON could not be read."], null, null, null, 0);
        }

        try
        {
            using var doc = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 64 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var collectionType) || collectionType.ValueKind != JsonValueKind.String || collectionType.GetString() != "FeatureCollection" || !root.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array)
                return new ImportValidation(0, ["GeoJSON must be a FeatureCollection."], null, null, null, 0);
            if (features.GetArrayLength() > 5000) return new ImportValidation(features.GetArrayLength(), ["GeoJSON exceeds 5000 features."], null, null, null, 0);
            if (features.GetArrayLength() == 0) return new ImportValidation(0, ["GeoJSON has no features."], null, null, null, 0);

            var externalIds = new HashSet<string>(StringComparer.Ordinal);
            var selectedFeatures = new List<JsonElement>();
            var inferredExternalIds = 0;
            var index = 0;
            foreach (var feature in features.EnumerateArray())
            {
                index++;
                if (feature.ValueKind != JsonValueKind.Object)
                {
                    errors.Add($"Feature {index}: feature must be an object.");
                    continue;
                }

                var featureIdentifiers = ExtractExternalIds(feature, errors, index);
                if (featureIdentifiers.Count == 0) inferredExternalIds++;
                foreach (var identifier in featureIdentifiers)
                {
                    if (!externalIds.Add(identifier)) errors.Add($"Feature {index}: duplicate external id '{identifier}'.");
                }

                if (!MatchesSelectedLayer(feature, layerKind, out var layerError))
                {
                    if (layerError is not null) errors.Add($"Feature {index}: {layerError}");
                    continue;
                }

                if (!feature.TryGetProperty("geometry", out var geometry))
                {
                    errors.Add($"Feature {index}: geometry must be supplied.");
                    continue;
                }
                if (!ValidateGeometry(geometry, out var geometryError))
                {
                    errors.Add($"Feature {index}: {geometryError ?? "geometry is unsupported."}");
                    continue;
                }
                selectedFeatures.Add(feature.Clone());
            }

            if (selectedFeatures.Count == 0 && errors.Count == 0) errors.Add($"GeoJSON contains no features for selected {layerKind} layer.");
            var filteredPayload = SerializeFeatureCollection(selectedFeatures);
            return new ImportValidation(selectedFeatures.Count, errors, filteredPayload, null, null, inferredExternalIds);
        }
        catch (JsonException)
        {
            return new ImportValidation(0, ["GeoJSON is invalid or exceeds the maximum JSON depth of 64."], null, null, null, 0);
        }
    }

    private async Task<ImportValidation> ValidateCsvAsync(string path, CancellationToken ct)
    {
        var errors = new List<string>();
        var rows = new List<Dictionary<string, string>>();
        try
        {
            var config = new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                HasHeaderRecord = true,
                TrimOptions = TrimOptions.Trim,
                PrepareHeaderForMatch = args => args.Header.Trim(),
                MissingFieldFound = null
            };
            await using var stream = System.IO.File.OpenRead(path);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            using var csv = new CsvReader(reader, config);
            if (!await csv.ReadAsync()) return new ImportValidation(0, ["CSV has no header row."], null, null, null, 0);
            if (!csv.ReadHeader()) return new ImportValidation(0, ["CSV has no header row."], null, null, null, 0);
            var headers = csv.HeaderRecord?.Select(x => x.Trim()).ToArray() ?? [];
            if (headers.Length == 0) return new ImportValidation(0, ["CSV has no headers."], null, null, null, 0);
            foreach (var duplicate in headers.GroupBy(x => x, StringComparer.Ordinal).Where(x => x.Count() > 1)) errors.Add($"CSV has duplicate header '{duplicate.Key}'.");
            foreach (var header in headers.Where(x => !CsvHeaders.Contains(x))) errors.Add($"CSV header '{header}' is not supported.");
            foreach (var required in new[] { "code", "name", "category" }.Where(x => !headers.Contains(x, StringComparer.Ordinal))) errors.Add($"CSV header '{required}' is required.");
            if (errors.Count > 0) return new ImportValidation(0, errors, null, null, null, 0);

            while (await csv.ReadAsync())
            {
                var row = headers.ToDictionary(header => header, header => csv.GetField(header) ?? "", StringComparer.Ordinal);
                rows.Add(row);
                if (rows.Count > 5000)
                {
                    errors.Add("CSV exceeds 5000 rows.");
                    break;
                }
            }
            if (rows.Count == 0) errors.Add("CSV has no data rows.");
        }
        catch (CsvHelperException ex)
        {
            errors.Add($"CSV parsing failed near row {ex.Context?.Parser?.Row ?? 0}: {ex.Message}");
        }
        catch (Exception ex)
        {
            errors.Add($"CSV parsing failed: {ex.Message}");
        }
        if (errors.Count > 0) return new ImportValidation(rows.Count, errors, null, null, null, 0);

        var zones = await db.ParkZones.AsNoTracking().ToListAsync(ct);
        var codeValues = rows.Select(row => Value(row, "code").Trim()).Where(code => code.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        var publicCodeValues = codeValues.Select(PublicCodeFor).Distinct(StringComparer.Ordinal).ToArray();
        var deviceCodeValues = rows.Where(row => Value(row, "category").Trim() == "Sensor").Select(row => Value(row, "deviceCode").Trim()).Where(code => code.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        var existingCodes = (await db.Assets.AsNoTracking().Where(x => codeValues.Contains(x.Code)).Select(x => x.Code).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        var existingPublicCodes = (await db.Assets.AsNoTracking().Where(x => publicCodeValues.Contains(x.PublicCode)).Select(x => x.PublicCode).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        var existingDeviceCodes = (await db.Devices.AsNoTracking().Where(x => deviceCodeValues.Contains(x.Code)).Select(x => x.Code).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        var seenCodes = new HashSet<string>(StringComparer.Ordinal);
        var seenPublicCodes = new HashSet<string>(StringComparer.Ordinal);
        var seenDeviceCodes = new HashSet<string>(StringComparer.Ordinal);
        var importedRows = new List<CsvImportRow>();

        for (var index = 0; index < rows.Count; index++)
        {
            var rowNumber = index + 2;
            var row = rows[index];
            var rowErrors = new List<string>();
            var code = Value(row, "code").Trim();
            var name = Value(row, "name").Trim();
            var category = Value(row, "category").Trim();
            var publicDescription = Value(row, "publicDescription").Trim();
            if (code.Length == 0 || name.Length == 0 || category is not ("Sensor" or "Facility" or "Plant")) rowErrors.Add("code, name and valid category are required.");
            var publicCode = PublicCodeFor(code);
            if (code.Length > 0 && (!seenCodes.Add(code) || existingCodes.Contains(code))) rowErrors.Add($"duplicate code {code}.");
            if (code.Length > 0 && (!seenPublicCodes.Add(publicCode) || existingPublicCodes.Contains(publicCode))) rowErrors.Add($"public code collision {publicCode}.");

            var hasLongitude = HasValue(row, "longitude");
            var hasLatitude = HasValue(row, "latitude");
            decimal? longitude = null;
            decimal? latitude = null;
            if (hasLongitude != hasLatitude) rowErrors.Add("longitude and latitude must be supplied together.");
            if (hasLongitude && (!decimal.TryParse(Value(row, "longitude"), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedLongitude) || parsedLongitude is < -180 or > 180)) rowErrors.Add("invalid longitude."); else if (hasLongitude) longitude = decimal.Parse(Value(row, "longitude"), NumberStyles.Number, CultureInfo.InvariantCulture);
            if (hasLatitude && (!decimal.TryParse(Value(row, "latitude"), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedLatitude) || parsedLatitude is < -90 or > 90)) rowErrors.Add("invalid latitude."); else if (hasLatitude) latitude = decimal.Parse(Value(row, "latitude"), NumberStyles.Number, CultureInfo.InvariantCulture);

            var zoneId = ResolveZone(row, zones, out var zoneError);
            if (zoneError is not null) rowErrors.Add(zoneError);

            var species = Value(row, "species").Trim();
            decimal? diameterCm = null;
            decimal? heightM = null;
            if (HasValue(row, "diameterCm") && (!decimal.TryParse(Value(row, "diameterCm"), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedDiameter) || parsedDiameter <= 0)) rowErrors.Add("diameterCm must be a positive number when supplied."); else if (HasValue(row, "diameterCm")) diameterCm = decimal.Parse(Value(row, "diameterCm"), NumberStyles.Number, CultureInfo.InvariantCulture);
            if (HasValue(row, "heightM") && (!decimal.TryParse(Value(row, "heightM"), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedHeight) || parsedHeight <= 0)) rowErrors.Add("heightM must be a positive number when supplied."); else if (HasValue(row, "heightM")) heightM = decimal.Parse(Value(row, "heightM"), NumberStyles.Number, CultureInfo.InvariantCulture);

            var facilityKind = Value(row, "kind").Trim();
            var deviceType = Value(row, "deviceType").Trim();
            var deviceCode = Value(row, "deviceCode").Trim();
            if (category == "Sensor")
            {
                if (deviceType.Length == 0 || deviceCode.Length == 0) rowErrors.Add("Sensor rows require deviceType and deviceCode.");
                else if (!seenDeviceCodes.Add(deviceCode) || existingDeviceCodes.Contains(deviceCode)) rowErrors.Add($"duplicate deviceCode {deviceCode}.");
            }
            else if (deviceType.Length > 0 || deviceCode.Length > 0) rowErrors.Add("deviceType and deviceCode are only supported for Sensor rows.");

            if (rowErrors.Count > 0)
            {
                errors.AddRange(rowErrors.Select(error => $"Row {rowNumber}: {error}"));
                continue;
            }
            importedRows.Add(new CsvImportRow(code, name, category, zoneId, longitude, latitude, publicDescription, species.Length == 0 ? null : species, diameterCm, heightM, facilityKind.Length == 0 ? null : facilityKind, deviceType.Length == 0 ? null : deviceType, deviceCode.Length == 0 ? null : deviceCode, publicCode));
        }

        return new ImportValidation(rows.Count, errors, null, null, importedRows, 0);
    }

    private static bool HasCompatibleExtension(string type, string fileName) => (type, Path.GetExtension(Path.GetFileName(fileName)).ToLowerInvariant()) switch
    {
        ("CSV", ".csv") => true,
        ("GeoJSON", ".geojson" or ".json") => true,
        ("DOM", ".png" or ".jpg" or ".jpeg") => true,
        _ => false
    };

    private static string Value(IReadOnlyDictionary<string, string> row, string key) => row.TryGetValue(key, out var value) ? value : "";
    private static bool HasValue(IReadOnlyDictionary<string, string> row, string key) => !string.IsNullOrWhiteSpace(Value(row, key));
    private static string PublicCodeFor(string code) => "P-" + code.ToUpperInvariant().Replace(" ", "-");

    private static Guid? ResolveZone(IReadOnlyDictionary<string, string> row, IReadOnlyList<ParkZone> zones, out string? error)
    {
        error = null;
        var selectors = new List<(string Name, string Value)>();
        foreach (var header in new[] { "zone", "zoneId", "zoneName", "zoneCode" }) if (HasValue(row, header)) selectors.Add((header, Value(row, header).Trim()));
        if (selectors.Count == 0) return null;

        var matches = new List<ParkZone>();
        foreach (var selector in selectors)
        {
            var matched = selector.Name switch
            {
                "zoneId" => Guid.TryParse(selector.Value, out var id) ? zones.Where(z => z.Id == id).ToList() : [],
                "zoneName" => zones.Where(z => string.Equals(z.Name, selector.Value, StringComparison.Ordinal)).ToList(),
                "zoneCode" => zones.Where(z => string.Equals(z.Code, selector.Value, StringComparison.Ordinal)).ToList(),
                _ => Guid.TryParse(selector.Value, out var id) ? zones.Where(z => z.Id == id).ToList() : zones.Where(z => string.Equals(z.Code, selector.Value, StringComparison.Ordinal) || string.Equals(z.Name, selector.Value, StringComparison.Ordinal)).ToList()
            };
            if (matched.Count == 0)
            {
                error = $"unknown zone '{selector.Value}'.";
                return null;
            }
            matches.AddRange(matched);
        }

        var resolved = matches.Select(zone => zone.Id).Distinct().ToArray();
        if (resolved.Length != 1)
        {
            error = "zone selectors are ambiguous or disagree.";
            return null;
        }
        return resolved[0];
    }

    private static bool TryNormalizeBounds(JsonElement root, out string normalized)
    {
        normalized = "";
        decimal southLat, westLng, northLat, eastLng;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("southWest", out var southWest) && root.TryGetProperty("northEast", out var northEast) && TryReadLngLatObject(southWest, out westLng, out southLat) && TryReadLngLatObject(northEast, out eastLng, out northLat))
        {
        }
        else if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() == 2 && TryReadLatLngArray(root[0], out southLat, out westLng) && TryReadLatLngArray(root[1], out northLat, out eastLng))
        {
        }
        else return false;

        if (westLng is < -180 or > 180 || eastLng is < -180 or > 180 || southLat is < -90 or > 90 || northLat is < -90 or > 90 || westLng >= eastLng || southLat >= northLat) return false;
        normalized = JsonSerializer.Serialize(new { southWest = new { lng = westLng, lat = southLat }, northEast = new { lng = eastLng, lat = northLat } });
        return true;
    }

    private static bool TryReadLngLatObject(JsonElement element, out decimal lng, out decimal lat)
    {
        lng = lat = 0;
        try { return element.ValueKind == JsonValueKind.Object && element.TryGetProperty("lng", out var lngValue) && element.TryGetProperty("lat", out var latValue) && lngValue.TryGetDecimal(out lng) && latValue.TryGetDecimal(out lat); }
        catch (FormatException) { return false; }
    }

    private static bool TryReadLatLngArray(JsonElement element, out decimal lat, out decimal lng)
    {
        lat = lng = 0;
        try { return element.ValueKind == JsonValueKind.Array && element.GetArrayLength() == 2 && element[0].TryGetDecimal(out lat) && element[1].TryGetDecimal(out lng); }
        catch (FormatException) { return false; }
    }

    private static bool MatchesSelectedLayer(JsonElement feature, string selectedLayer, out string? error)
    {
        error = null;
        if (!feature.TryGetProperty("properties", out var properties) || properties.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return true;
        if (properties.ValueKind != JsonValueKind.Object)
        {
            error = "properties must be an object when supplied.";
            return false;
        }
        foreach (var propertyName in new[] { "layer", "kind" })
        {
            if (!properties.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null) continue;
            if (value.ValueKind != JsonValueKind.String)
            {
                error = $"properties.{propertyName} must be a string when supplied.";
                return false;
            }
            if (!string.Equals(value.GetString()?.Trim(), selectedLayer, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    private static HashSet<string> ExtractExternalIds(JsonElement feature, ICollection<string> errors, int featureIndex)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (feature.TryGetProperty("id", out var id) && id.ValueKind != JsonValueKind.Null)
        {
            if (TryExternalId(id, out var externalId)) ids.Add(externalId);
            else errors.Add($"Feature {featureIndex}: id must be a string or number when supplied.");
        }
        if (feature.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty("externalId", out var propertyId) && propertyId.ValueKind != JsonValueKind.Null)
        {
            if (TryExternalId(propertyId, out var externalId)) ids.Add(externalId);
            else errors.Add($"Feature {featureIndex}: properties.externalId must be a string or number when supplied.");
        }
        return ids;
    }

    private static bool TryExternalId(JsonElement value, out string externalId)
    {
        externalId = "";
        if (value.ValueKind == JsonValueKind.String) externalId = value.GetString()?.Trim() ?? "";
        else if (value.ValueKind == JsonValueKind.Number) externalId = value.GetRawText();
        else return false;
        return externalId.Length > 0;
    }

    private static bool ValidateGeometry(JsonElement geometry, out string? error)
    {
        error = null;
        if (geometry.ValueKind != JsonValueKind.Object || !geometry.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String || !geometry.TryGetProperty("coordinates", out var coordinates))
        {
            error = "geometry must include type and coordinates.";
            return false;
        }
        return typeElement.GetString() switch
        {
            "Point" => ValidatePosition(coordinates, out error),
            "LineString" => ValidateLineString(coordinates, out error),
            "Polygon" => ValidatePolygon(coordinates, out error),
            "MultiPoint" => ValidateMultiPoint(coordinates, out error),
            "MultiLineString" => ValidateMultiLineString(coordinates, out error),
            "MultiPolygon" => ValidateMultiPolygon(coordinates, out error),
            _ => UnsupportedGeometry(out error)
        };
    }

    private static bool UnsupportedGeometry(out string? error) { error = "geometry type is unsupported."; return false; }

    private static bool ValidatePosition(JsonElement position, out string? error)
    {
        error = null;
        if (position.ValueKind != JsonValueKind.Array || position.GetArrayLength() < 2)
        {
            error = "coordinate positions require longitude and latitude.";
            return false;
        }
        try
        {
            if (!position[0].TryGetDecimal(out var longitude) || !position[1].TryGetDecimal(out var latitude) || longitude is < -180 or > 180 || latitude is < -90 or > 90)
            {
                error = "coordinates are outside WGS84 bounds.";
                return false;
            }
            foreach (var dimension in position.EnumerateArray()) if (dimension.ValueKind != JsonValueKind.Number || !dimension.TryGetDecimal(out _)) { error = "coordinate dimensions must be finite numbers."; return false; }
            return true;
        }
        catch (FormatException)
        {
            error = "coordinate dimensions must be finite numbers.";
            return false;
        }
    }

    private static bool ValidateLineString(JsonElement line, out string? error)
    {
        error = null;
        if (line.ValueKind != JsonValueKind.Array || line.GetArrayLength() < 2) { error = "LineString requires at least two positions."; return false; }
        foreach (var position in line.EnumerateArray()) if (!ValidatePosition(position, out error)) return false;
        return true;
    }

    private static bool ValidatePolygon(JsonElement polygon, out string? error)
    {
        error = null;
        if (polygon.ValueKind != JsonValueKind.Array || polygon.GetArrayLength() == 0) { error = "Polygon requires at least one linear ring."; return false; }
        foreach (var ring in polygon.EnumerateArray()) if (!ValidateRing(ring, out error)) return false;
        return true;
    }

    private static bool ValidateMultiPoint(JsonElement points, out string? error)
    {
        error = null;
        if (points.ValueKind != JsonValueKind.Array || points.GetArrayLength() == 0) { error = "MultiPoint requires at least one position."; return false; }
        foreach (var point in points.EnumerateArray()) if (!ValidatePosition(point, out error)) return false;
        return true;
    }

    private static bool ValidateMultiLineString(JsonElement lines, out string? error)
    {
        error = null;
        if (lines.ValueKind != JsonValueKind.Array || lines.GetArrayLength() == 0) { error = "MultiLineString requires at least one line."; return false; }
        foreach (var line in lines.EnumerateArray()) if (!ValidateLineString(line, out error)) return false;
        return true;
    }

    private static bool ValidateMultiPolygon(JsonElement polygons, out string? error)
    {
        error = null;
        if (polygons.ValueKind != JsonValueKind.Array || polygons.GetArrayLength() == 0) { error = "MultiPolygon requires at least one polygon."; return false; }
        foreach (var polygon in polygons.EnumerateArray()) if (!ValidatePolygon(polygon, out error)) return false;
        return true;
    }

    private static bool ValidateRing(JsonElement ring, out string? error)
    {
        error = null;
        if (ring.ValueKind != JsonValueKind.Array || ring.GetArrayLength() < 4) { error = "Polygon rings require at least four closed positions."; return false; }
        foreach (var position in ring.EnumerateArray()) if (!ValidatePosition(position, out error)) return false;
        if (!SamePosition(ring[0], ring[ring.GetArrayLength() - 1])) { error = "Polygon rings must be closed."; return false; }
        var distinctPoints = ring.EnumerateArray().Take(ring.GetArrayLength() - 1).Select(position => $"{position[0].GetRawText()},{position[1].GetRawText()}").Distinct(StringComparer.Ordinal).Count();
        if (distinctPoints < 3) { error = "Polygon rings require three distinct positions."; return false; }
        return true;
    }

    private static bool SamePosition(JsonElement left, JsonElement right)
    {
        if (left.GetArrayLength() != right.GetArrayLength()) return false;
        for (var index = 0; index < left.GetArrayLength(); index++) if (left[index].GetRawText() != right[index].GetRawText()) return false;
        return true;
    }

    private static string SerializeFeatureCollection(IEnumerable<JsonElement> features)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "FeatureCollection");
            writer.WritePropertyName("features");
            writer.WriteStartArray();
            foreach (var feature in features) feature.WriteTo(writer);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string BuildPayloadMetadata(string fileKind, string originalFileName, Guid operatorId, DateTimeOffset previewedAt, string? boundsJson)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        writer.WriteString("fileKind", fileKind);
        writer.WriteString("originalFileName", originalFileName);
        writer.WriteString("operatorId", operatorId);
        writer.WriteString("previewedAt", previewedAt);
        if (boundsJson is not null)
        {
            using var bounds = JsonDocument.Parse(boundsJson);
            writer.WritePropertyName("bounds");
            bounds.RootElement.WriteTo(writer);
        }
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string? ReadBoundsFromPayload(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        try
        {
            using var payload = JsonDocument.Parse(payloadJson);
            if (payload.RootElement.ValueKind == JsonValueKind.Object && payload.RootElement.TryGetProperty("bounds", out var bounds)) return bounds.GetRawText();
            if (payload.RootElement.ValueKind == JsonValueKind.Object && payload.RootElement.TryGetProperty("southWest", out _)) return payload.RootElement.GetRawText();
        }
        catch (JsonException)
        {
        }
        return null;
    }

    private static string ContentType(string name) => Path.GetExtension(name).ToLowerInvariant() switch { ".geojson" => "application/geo+json", ".csv" => "text/csv", ".png" => "image/png", ".wav" => "audio/wav", _ => "application/octet-stream" };

    private sealed record CsvImportRow(string Code, string Name, string Category, Guid? ZoneId, decimal? Longitude, decimal? Latitude, string PublicDescription, string? Species, decimal? DiameterCm, decimal? HeightM, string? FacilityKind, string? DeviceType, string? DeviceCode, string PublicCode);
    private sealed record ImportValidation(int Rows, List<string> Errors, string? GeoJson, string? BoundsJson, List<CsvImportRow>? CsvRows, int InferredExternalIds);
}

[ApiController]
[Route("api/public")]
public sealed class PublicMapController(ParkDbContext db) : ControllerBase
{
    [AllowAnonymous, HttpGet("park")]
    public async Task<object> Park(CancellationToken ct)
    {
        var park = await db.Parks.AsNoTracking().Include(x => x.Zones).SingleOrDefaultAsync(ct) ?? throw new ApiException("Park not initialized", 404);
        return new { park.Name, park.Description, park.OpenHours, park.Phone, park.Longitude, park.Latitude, zones = park.Zones.Select(z => new { z.Name, z.Code, z.Description }) };
    }

    [AllowAnonymous, HttpGet("map")]
    public async Task<object> Map(CancellationToken ct) => new { layers = await db.MapLayers.AsNoTracking().Where(x => x.IsPublic).Select(x => new { x.Id, x.Name, x.Kind, geoJson = x.GeoJson, imageUrl = x.FileId == null ? null : "/api/files/" + x.FileId, bounds = x.BoundsJson }).ToListAsync(ct), assets = await db.Assets.AsNoTracking().Where(x => x.Status == "Active" && x.Longitude != null && x.Latitude != null).Select(x => new { x.Id, x.Name, x.Category, x.PublicCode, x.Longitude, x.Latitude }).ToListAsync(ct) };

    [AllowAnonymous, HttpGet("route")]
    public async Task<object> Route(Guid from, Guid to, CancellationToken ct)
    {
        var positions = await db.Assets.AsNoTracking().Where(x => x.Id == from || x.Id == to).Select(x => new { x.Id, x.Longitude, x.Latitude }).ToListAsync(ct);
        if (positions.Count != 2 || positions.Any(x => x.Longitude is null || x.Latitude is null)) throw new ApiException("Route unavailable", 422, "Both places must have a map position.");
        var roads = await db.MapLayers.AsNoTracking().Where(x => x.Kind == "Road" && x.GeoJson != null).Select(x => x.GeoJson!).ToListAsync(ct);
        var graph = RoadGraph.Build(roads); var start = graph.Snap(positions.Single(x => x.Id == from).Longitude!.Value, positions.Single(x => x.Id == from).Latitude!.Value); var end = graph.Snap(positions.Single(x => x.Id == to).Longitude!.Value, positions.Single(x => x.Id == to).Latitude!.Value);
        if (start is null || end is null || !graph.TryPath(start.Value, end.Value, out var path, out var meters)) throw new ApiException("Route unavailable", 422, "No available park road connects these locations.");
        var points = path.Select(p => new { longitude = p.Lng, latitude = p.Lat }).ToList();
        return new { distance = Math.Round(meters, 1), distanceMeters = Math.Round(meters, 1), points, geoJson = new { type = "LineString", coordinates = path.Select(p => new[] { p.Lng, p.Lat }) } };
    }
}

public sealed record ParkRequest(string Name, string? Description, string? OpenHours, string? Phone, decimal Longitude, decimal Latitude);
public sealed class ImportPreviewForm { public IFormFile? File { get; set; } public string Type { get; set; } = ""; public string LayerKind { get; set; } = ""; public string? Bounds { get; set; } }

internal sealed class RoadGraph
{
    internal readonly record struct Point(decimal Lng, decimal Lat);
    private readonly Dictionary<Point, List<(Point Target, double Distance)>> _edges = [];
    public static RoadGraph Build(IEnumerable<string> layers)
    {
        var graph = new RoadGraph();
        foreach (var layer in layers)
        {
            try
            {
                using var doc = JsonDocument.Parse(layer);
                foreach (var f in doc.RootElement.GetProperty("features").EnumerateArray())
                {
                    var geometry = f.GetProperty("geometry"); if (geometry.GetProperty("type").GetString() != "LineString") continue;
                    Point? previous = null; foreach (var coord in geometry.GetProperty("coordinates").EnumerateArray()) { var point = new Point(decimal.Round(coord[0].GetDecimal(), 6), decimal.Round(coord[1].GetDecimal(), 6)); if (previous is not null) graph.Add(previous.Value, point); previous = point; }
                }
            }
            catch (Exception) { }
        }
        return graph;
    }
    public Point? Snap(decimal lng, decimal lat) => _edges.Keys.Select(p => (p, d: Distance(p, new Point(lng, lat)))).Where(x => x.d <= 20).OrderBy(x => x.d).Select(x => (Point?)x.p).FirstOrDefault();
    public bool TryPath(Point start, Point end, out List<Point> path, out double distance)
    {
        var queue = new PriorityQueue<Point, double>(); var costs = new Dictionary<Point, double> { [start] = 0 }; var previous = new Dictionary<Point, Point>(); queue.Enqueue(start, 0);
        while (queue.TryDequeue(out var current, out var cost)) { if (current.Equals(end)) break; if (cost != costs[current]) continue; foreach (var edge in _edges.GetValueOrDefault(current, [])) { var candidate = cost + edge.Distance; if (!costs.TryGetValue(edge.Target, out var existing) || candidate < existing) { costs[edge.Target] = candidate; previous[edge.Target] = current; queue.Enqueue(edge.Target, candidate); } } }
        if (!costs.TryGetValue(end, out distance)) { path = []; return false; }
        path = [end]; while (!path[^1].Equals(start)) path.Add(previous[path[^1]]); path.Reverse(); return true;
    }
    private void Add(Point from, Point to) { var d = Distance(from, to); if (!_edges.TryGetValue(from, out var fromEdges)) _edges[from] = fromEdges = []; if (!_edges.TryGetValue(to, out var toEdges)) _edges[to] = toEdges = []; fromEdges.Add((to, d)); toEdges.Add((from, d)); }
    private static double Distance(Point a, Point b) { const double r = 6371000; var dLat = (double)(b.Lat - a.Lat) * Math.PI / 180; var dLng = (double)(b.Lng - a.Lng) * Math.PI / 180; var h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos((double)a.Lat * Math.PI / 180) * Math.Cos((double)b.Lat * Math.PI / 180) * Math.Sin(dLng / 2) * Math.Sin(dLng / 2); return 2 * r * Math.Asin(Math.Sqrt(h)); }
}
