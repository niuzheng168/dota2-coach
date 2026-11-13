import { AccessStore } from './access-store.ts';
import { createCoachServer } from './app-server.ts';

const store = await AccessStore.open();
const server = createCoachServer(store);
const host = process.env.HOST || '0.0.0.0';
server.listen(Number(process.env.PORT || 3210), host, () => {
  const address = server.address();
  const port = typeof address === 'object' && address ? address.port : process.env.PORT;
  const displayHost = host.includes(':') ? `[${host}]` : host;
  console.log(`DOTA2 Bilingual Voice Coach Room listening on http://${displayHost}:${port}`);
});
