# Build the dashboard and serve the static output through nginx, which also proxies /api to
# the API container. Node is a build-time dependency only — it isn't in the final image.

FROM node:22-alpine AS build
WORKDIR /app

# Lockfile first: npm ci is the slow layer and only depends on these two files.
COPY package.json package-lock.json ./
RUN npm ci

COPY . .
RUN npm run build

FROM nginx:alpine AS runtime
COPY --from=build /app/dist /usr/share/nginx/html
COPY nginx.conf /etc/nginx/conf.d/default.conf

EXPOSE 80
