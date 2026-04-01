FROM node:22-alpine AS builder

WORKDIR /app

COPY package.json package-lock.json* ./
RUN npm ci --ignore-scripts

COPY tsconfig.json tsoa.json ./
COPY src/ src/
RUN npm run build

FROM node:22-alpine

WORKDIR /app

ENV NODE_ENV=production

COPY package.json package-lock.json* ./
RUN npm ci --ignore-scripts --omit=dev && npm cache clean --force

COPY --from=builder /app/dist dist/
COPY --from=builder /app/src/generated/swagger.json dist/generated/swagger.json

EXPOSE 8080
USER node

CMD ["node", "dist/index.js"]
