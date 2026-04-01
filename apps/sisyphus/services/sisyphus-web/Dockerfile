FROM node:20-alpine AS sdk-builder
WORKDIR /sdk

# Build oauth-core
COPY NyxID/sdk/oauth-core ./oauth-core
WORKDIR /sdk/oauth-core
RUN npm install && npx tsc

# Build oauth-react
WORKDIR /sdk
COPY NyxID/sdk/oauth-react ./oauth-react
WORKDIR /sdk/oauth-react
RUN mkdir -p node_modules/@nyxids && ln -s /sdk/oauth-core node_modules/@nyxids/oauth-core
RUN npm install && npx tsc

FROM node:20-alpine AS build
WORKDIR /app

# Place SDK source at the absolute path Vite's source maps expect
COPY --from=sdk-builder /sdk/oauth-core /NyxID/sdk/oauth-core
COPY --from=sdk-builder /sdk/oauth-react /NyxID/sdk/oauth-react

# Copy all source
COPY aevatar/apps/sisyphus/services/sisyphus-web/ .
RUN rm -rf node_modules package-lock.json

# Strip file: deps
RUN sed -i '/@nyxids\/oauth-core/d' package.json && \
    sed -i '/@nyxids\/oauth-react/d' package.json

RUN npm install --no-package-lock

# Place built SDKs into node_modules
COPY --from=sdk-builder /sdk/oauth-core ./node_modules/@nyxids/oauth-core
COPY --from=sdk-builder /sdk/oauth-react ./node_modules/@nyxids/oauth-react

RUN npm run build

FROM nginx:alpine
COPY --from=build /app/dist /usr/share/nginx/html
COPY --from=build /app/nginx.conf /etc/nginx/conf.d/default.conf
EXPOSE 80
