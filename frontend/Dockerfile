FROM node:22-alpine AS build
WORKDIR /app/frontend
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci
COPY frontend/ ./
RUN npm run build

FROM nginx:1.27-alpine
RUN addgroup -S app && adduser -S app -G app \
  && mkdir -p /var/cache/nginx /var/run \
  && chown -R app:app /var/cache/nginx /var/run /usr/share/nginx/html \
  && sed -i 's@/var/run/nginx.pid@/tmp/nginx.pid@g; s@/run/nginx.pid@/tmp/nginx.pid@g' /etc/nginx/nginx.conf
COPY frontend/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build --chown=app:app /app/frontend/dist /usr/share/nginx/html
USER app
EXPOSE 8080
