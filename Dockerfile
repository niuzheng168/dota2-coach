FROM node:24-bookworm-slim@sha256:dc26ae92c9fb15cf6b1d63e20f0944b2efe0f115b20e00be48fb23fceb061c54

ENV NODE_ENV=production
WORKDIR /app

COPY package.json package-lock.json ./
RUN npm ci --omit=dev --ignore-scripts && npm cache clean --force

COPY src/ ./src/
COPY public/ ./public/
COPY data/dota2-dictionary.json ./data/dota2-dictionary.json
COPY data/dota2-combat.json data/dota2-map.json data/dota2-camp-compositions.json ./data/
COPY docs/dota2-bilingual-dictionary.md ./docs/dota2-bilingual-dictionary.md
COPY scripts/create-user.ts ./scripts/create-user.ts

USER node
CMD ["node", "src/server.ts"]
