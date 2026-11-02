FROM node:22 AS build
WORKDIR /app
ARG NUXT_PUBLIC_API_BASE=https://localhost
ENV NUXT_PUBLIC_API_BASE=$NUXT_PUBLIC_API_BASE
COPY package*.json ./
RUN npm ci --no-audit --no-fund
COPY . .
RUN npx nuxt generate

FROM nginx:1.27
COPY --from=build /app/.output/public /usr/share/nginx/html
RUN printf 'server { listen 80; root /usr/share/nginx/html; location / { try_files $uri $uri/ /index.html; } }' > /etc/nginx/conf.d/default.conf
