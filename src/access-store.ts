import crypto from 'node:crypto';
import fs from 'node:fs/promises';
import path from 'node:path';
import type { RoomSettings } from './room-settings.ts';

export class AppError extends Error {
  status: number;
  constructor(status: number, message: string) {
    super(message);
    this.status = status;
  }
}

export interface PublicUser {
  username: string;
  canCreateRooms: boolean;
}

interface UserRecord extends PublicUser {
  salt: string;
  passwordHash: string;
}

export interface SessionRecord {
  idHash: string;
  username: string;
  csrfToken: string;
  expiresAt: number;
}

export interface StoredRoom {
  roomId: string;
  name: string;
  ownerUsername: string;
  allowedUsernames: string[];
  inviteToken: string;
  gsiToken: string;
  settings: RoomSettings;
  createdAt: number;
}

interface Database {
  version: 1;
  users: UserRecord[];
  sessions: SessionRecord[];
  rooms: StoredRoom[];
}

export const SESSION_MAX_AGE_SECONDS = 7 * 24 * 60 * 60;
const token = () => crypto.randomBytes(32).toString('base64url');
export const hashToken = (value: string) => crypto.createHash('sha256').update(value).digest('hex');

export function secretsEqual(a: unknown, b: string): boolean {
  return typeof a === 'string' && a.length <= 1024 &&
    crypto.timingSafeEqual(Buffer.from(hashToken(a), 'hex'), Buffer.from(hashToken(b), 'hex'));
}

function derivePassword(password: string, salt: string): Promise<Buffer> {
  return new Promise((resolve, reject) => {
    crypto.scrypt(password, salt, 64, { N: 32768, r: 8, p: 1, maxmem: 64 * 1024 * 1024 },
      (error, key) => error ? reject(error) : resolve(key));
  });
}

function publicUser(user: UserRecord): PublicUser {
  return { username: user.username, canCreateRooms: user.canCreateRooms };
}

/** Single-process durable storage. Never serve this directory as static content. */
export class AccessStore {
  private data: Database;
  private pending: Promise<void> = Promise.resolve();
  private filename: string;

  private constructor(directory: string, data: Database) {
    this.filename = path.join(directory, 'access.json');
    this.data = data;
  }

  static async open(directory = process.env.APP_DATA_DIR || path.resolve('.local')): Promise<AccessStore> {
    await fs.mkdir(directory, { recursive: true, mode: 0o700 });
    let data: Database;
    try {
      data = JSON.parse(await fs.readFile(path.join(directory, 'access.json'), 'utf8'));
      if (data.version !== 1 || !Array.isArray(data.users) ||
          !Array.isArray(data.sessions) || !Array.isArray(data.rooms)) {
        throw new Error('Unsupported or invalid access database');
      }
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code !== 'ENOENT') throw error;
      data = { version: 1, users: [], sessions: [], rooms: [] };
    }
    return new AccessStore(directory, data);
  }

  private mutate<T>(change: (data: Database) => T): Promise<T> {
    const operation = this.pending.then(async () => {
      const next = structuredClone(this.data);
      const result = change(next);
      const temporary = `${this.filename}.${crypto.randomUUID()}.tmp`;
      try {
        await fs.writeFile(temporary, JSON.stringify(next, null, 2), { mode: 0o600, flag: 'wx' });
        await fs.rename(temporary, this.filename);
      } finally {
        await fs.rm(temporary, { force: true });
      }
      this.data = next;
      return structuredClone(result);
    });
    this.pending = operation.then(() => {}, () => {});
    return operation;
  }

  listUsers(): PublicUser[] {
    return this.data.users.map(publicUser);
  }

  async createUser(username: string, password: string, canCreateRooms = false): Promise<PublicUser> {
    if (!/^[A-Za-z0-9_-]{1,32}$/.test(username) || password.length < 6 || password.length > 256) {
      throw new AppError(400, '用户名须为 1–32 位字母、数字、下划线或连字符；密码须为 6–256 位');
    }
    const salt = crypto.randomBytes(16).toString('hex');
    const passwordHash = (await derivePassword(password, salt)).toString('hex');
    return this.mutate(data => {
      if (data.users.some(user => user.username === username)) throw new AppError(409, '账号已存在');
      const user = { username, canCreateRooms, salt, passwordHash };
      data.users.push(user);
      return publicUser(user);
    });
  }

  async verifyPassword(username: string, password: string): Promise<PublicUser | null> {
    const user = this.data.users.find(user => user.username === username);
    // Unknown users incur the same key derivation work as known users.
    const actual = await derivePassword(password, user?.salt || '00000000000000000000000000000000');
    const expected = user ? Buffer.from(user.passwordHash, 'hex') : Buffer.alloc(64);
    return crypto.timingSafeEqual(actual, expected) && user ? publicUser(user) : null;
  }

  async createSession(username: string): Promise<{ sessionToken: string; session: SessionRecord }> {
    const sessionToken = token();
    const session = {
      idHash: hashToken(sessionToken), username, csrfToken: token(),
      expiresAt: Date.now() + SESSION_MAX_AGE_SECONDS * 1000,
    };
    await this.mutate(data => {
      if (!data.users.some(user => user.username === username)) throw new AppError(401, '请先登录');
      data.sessions = data.sessions.filter(item => item.expiresAt > Date.now());
      data.sessions.push(session);
    });
    return { sessionToken, session };
  }

  getSession(sessionToken: string | undefined): { session: SessionRecord; user: PublicUser } | null {
    if (!sessionToken || !/^[A-Za-z0-9_-]{43}$/.test(sessionToken)) return null;
    const session = this.data.sessions.find(item => item.idHash === hashToken(sessionToken) && item.expiresAt > Date.now());
    const user = session && this.data.users.find(item => item.username === session.username);
    return session && user ? { session: structuredClone(session), user: publicUser(user) } : null;
  }

  async deleteSession(idHash: string): Promise<void> {
    await this.mutate(data => { data.sessions = data.sessions.filter(item => item.idHash !== idHash); });
  }

  listOwnedRooms(username: string): StoredRoom[] {
    return structuredClone(this.data.rooms.filter(room => room.ownerUsername === username));
  }

  getRoom(roomId: string): StoredRoom | undefined {
    return structuredClone(this.data.rooms.find(room => room.roomId === roomId));
  }

  requireRoom(user: PublicUser, roomId: string, invitation?: string): StoredRoom {
    const room = this.getRoom(roomId);
    if (!room) throw new AppError(404, '房间不存在，请向房主索取新的邀请链接');
    if (room.ownerUsername !== user.username &&
        (!room.allowedUsernames.includes(user.username) || !secretsEqual(invitation, room.inviteToken))) {
      throw new AppError(403, '需要有效邀请链接，且房主必须允许你的账号加入');
    }
    return room;
  }

  requireOwner(user: PublicUser, roomId: string): StoredRoom {
    const room = this.getRoom(roomId);
    if (!room) throw new AppError(404, '房间不存在');
    if (room.ownerUsername !== user.username) throw new AppError(403, '只有房主可以执行此操作');
    return room;
  }

  private validateAllowed(data: Database, owner: string, value: unknown): string[] {
    if (!Array.isArray(value) || value.length > 100 ||
        value.some(name => typeof name !== 'string' || !data.users.some(user => user.username === name))) {
      throw new AppError(400, '允许名单必须由已存在的账号组成');
    }
    return [...new Set(value as string[])].filter(name => name !== owner);
  }

  async createRoom(user: PublicUser, name: string, settings: RoomSettings, allowed: unknown): Promise<StoredRoom> {
    return this.mutate(data => {
      if (!data.users.find(item => item.username === user.username)?.canCreateRooms) {
        throw new AppError(403, '你的账号没有创建房间的权限');
      }
      if (!name.trim() || name.trim().length > 64) throw new AppError(400, '房间名称须为 1–64 个字符');
      const room: StoredRoom = {
        roomId: crypto.randomUUID(), name: name.trim(), ownerUsername: user.username,
        settings: structuredClone(settings), createdAt: Date.now(),
        allowedUsernames: this.validateAllowed(data, user.username, allowed),
        inviteToken: token(), gsiToken: token(),
      };
      data.rooms.push(room);
      return room;
    });
  }

  async updateAccess(user: PublicUser, roomId: string, allowed: unknown): Promise<StoredRoom> {
    return this.mutate(data => {
      const room = data.rooms.find(item => item.roomId === roomId);
      if (!room) throw new AppError(404, '房间不存在');
      if (room.ownerUsername !== user.username) throw new AppError(403, '只有房主可以修改允许名单');
      room.allowedUsernames = this.validateAllowed(data, user.username, allowed);
      return room;
    });
  }
}
