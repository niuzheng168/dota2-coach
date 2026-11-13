import { AccessStore } from '../src/access-store.ts';

// Read credentials from stdin, never from command-line arguments or a tracked seed file.
let input = '';
for await (const chunk of process.stdin) {
  input += chunk;
  if (input.length > 4096) throw new Error('Input is too large');
}
const { username, password, canCreateRooms = false } = JSON.parse(input);
if (typeof username !== 'string' || typeof password !== 'string' || typeof canCreateRooms !== 'boolean') {
  throw new Error('Expected JSON with username, password, and optional canCreateRooms');
}
const store = await AccessStore.open();
console.log(JSON.stringify(await store.createUser(username, password, canCreateRooms)));
