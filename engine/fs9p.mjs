/*
 * hv86 - сервер протокола 9P2000.L для общей папки.
 *
 * Здесь только протокол: разбор кадров, fid'ы, qid и таблица операций.
 * Файловые операции делает адаптер (`fs`):
 *   - engine/fs-node.mjs  - node:fs (headless-движок);
 *   - engine/fs-xhr.mjs   - синхронные POST к нашему же серверу (web-движок).
 *
 * Пути внутри сервера - относительные, через "/" ("" = корень шары).
 * Хост-пути и проверки "не выйти наружу" живут в адаптере: так они не
 * дублируются и не зависят от node:path (которого нет в браузере).
 */

const TVERSION = 100, RVERSION = 101;
const TAUTH = 102, RAUTH = 103;
const TATTACH = 104, RATTACH = 105;
const TFLUSH = 108, RFLUSH = 109;
const TWALK = 110, RWALK = 111;
const TREAD = 116, RREAD = 117;
const TWRITE = 118, RWRITE = 119;
const TCLUNK = 120, RCLUNK = 121;
const TREMOVE = 122, RREMOVE = 123;

const TLOPEN = 12, RLOPEN = 13;
const TLCREATE = 14, RLCREATE = 15;
const TREADLINK = 22, RREADLINK = 23;
const TGETATTR = 24, RGETATTR = 25;
const TSETATTR = 26, RSETATTR = 27;
const TREADDIR = 40, RREADDIR = 41;
const TFSYNC = 50, RFSYNC = 51;
const TSTATFS = 8, RSTATFS = 9;
const TMKDIR = 72, RMKDIR = 73;
const TRENAMEAT = 74, RRENAMEAT = 75;
const TUNLINKAT = 76, RUNLINKAT = 77;
const TLINK = 70;
const TSYMLINK = 16;
const TGETLOCK = 54;
const TLOCK = 52;

const RLERROR = 7;

const QTDIR = 0x80;
const S_IFDIR = 0x4000;
const S_IFREG = 0x8000;

const ENOENT = 2, EIO = 5, EACCES = 13, EEXIST = 17, ENOTDIR = 20, EISDIR = 21;
const EINVAL = 22, ENOTEMPTY = 39, EROFS = 30, EOPNOTSUPP = 95;

const DEFAULT_MSIZE = 8192;
const O_TRUNC = 0x200;

// в браузере нет Buffer: работаем Uint8Array + TextEncoder
const encoder = new TextEncoder();
const decoder = new TextDecoder();
const AT_REMOVEDIR = 0x200;

// ------------------------------------------------------------------- буферы

class Reader {
    constructor(buffer, offset = 0) {
        this.buffer = buffer;
        this.view = new DataView(buffer.buffer, buffer.byteOffset, buffer.byteLength);
        this.offset = offset;
    }

    u8() { return this.view.getUint8(this.offset++); }
    u16() { const v = this.view.getUint16(this.offset, true); this.offset += 2; return v; }
    u32() { const v = this.view.getUint32(this.offset, true); this.offset += 4; return v; }
    u64() { const v = this.view.getBigUint64(this.offset, true); this.offset += 8; return Number(v); }

    str() {
        const length = this.u16();
        const bytes = this.buffer.subarray(this.offset, this.offset + length);
        this.offset += length;
        return decoder.decode(bytes);
    }

    rest() {
        const bytes = this.buffer.subarray(this.offset);
        this.offset = this.buffer.length;
        return new Uint8Array(bytes);
    }
}

class Writer {
    constructor(size = 512) {
        this.buffer = new Uint8Array(size);
        this.view = new DataView(this.buffer.buffer);
        this.offset = 0;
    }

    ensure(extra) {
        if (this.offset + extra <= this.buffer.length) return;

        let size = this.buffer.length * 2;
        while (size < this.offset + extra) size *= 2;

        const next = new Uint8Array(size);
        next.set(this.buffer.subarray(0, this.offset));

        this.buffer = next;
        this.view = new DataView(next.buffer);
    }

    u8(value) { this.ensure(1); this.view.setUint8(this.offset, value & 0xFF); this.offset += 1; return this; }
    u16(value) { this.ensure(2); this.view.setUint16(this.offset, value & 0xFFFF, true); this.offset += 2; return this; }
    u32(value) { this.ensure(4); this.view.setUint32(this.offset, value >>> 0, true); this.offset += 4; return this; }

    u64(value) {
        this.ensure(8);
        const big = typeof value === "bigint" ? value : BigInt(Math.trunc(value));
        this.view.setBigUint64(this.offset, big, true);
        this.offset += 8;
        return this;
    }

    str(value) {
        const bytes = encoder.encode(value ?? "");
        this.u16(bytes.length);
        this.ensure(bytes.length);
        this.buffer.set(bytes, this.offset);
        this.offset += bytes.length;
        return this;
    }

    bytes(value) {
        const bytes = value instanceof Uint8Array ? value : new Uint8Array(value);
        this.ensure(bytes.length);
        this.buffer.set(bytes, this.offset);
        this.offset += bytes.length;
        return this;
    }

    qid(qid) {
        this.u8(qid.type);
        this.u32(qid.version);
        this.u64(qid.path);
        return this;
    }

    toBuffer() { return this.buffer.subarray(0, this.offset); }
}

// -------------------------------------------------------------------- сервер

/**
 * @param {object} options
 *   fs       - адаптер файловых операций (обязателен)
 *   readonly - запретить запись
 *   log      - функция логирования
 */
export function createShareHandler(options = {}) {
    const fsApi = options.fs;

    if (!fsApi) throw new Error("createShareHandler: fs adapter is required");

    const readonly = !!options.readonly;
    const log = options.log || (() => {});

    const fids = new Map();
    const qidPaths = new Map();
    let nextQid = 2;

    // ------------------------------------------------------------ утилиты

    function qidPathFor(relative) {
        const key = relative === "" ? "." : relative.toLowerCase();

        if (qidPaths.has(key)) return qidPaths.get(key);

        let hash = 0xcbf29ce484222325n;
        const bytes = encoder.encode(key);

        for (const byte of bytes) {
            hash ^= BigInt(byte);
            hash = (hash * 0x100000001b3n) & 0xFFFFFFFFFFFFFFFFn;
        }

        if (hash === 0n) hash = BigInt(nextQid++);

        qidPaths.set(key, hash);
        return hash;
    }

    function qidOf(relative, stats) {
        return {
            type: stats.dir ? QTDIR : 0,
            version: 0,
            path: qidPathFor(relative)
        };
    }

    function error(errno, message) {
        const e = new Error(message || "error");
        e.p9errno = errno;
        return e;
    }

    function errnoOf(e) {
        if (e && e.p9errno) return e.p9errno;

        switch (e && e.code) {
            case "ENOENT": return ENOENT;
            case "EACCES":
            case "EPERM": return EACCES;
            case "EEXIST": return EEXIST;
            case "ENOTDIR": return ENOTDIR;
            case "EISDIR": return EISDIR;
            case "EINVAL": return EINVAL;
            case "ENOTEMPTY": return ENOTEMPTY;
            case "EROFS": return EROFS;
            case "ENOSYS":
            case "ENOTSUP": return EOPNOTSUPP;
            default: return EIO;
        }
    }

    function need(condition, errno, message) {
        if (!condition) throw error(errno, message);
    }

    function requireWritable() {
        need(!readonly, EROFS, "share is read-only");
    }

    function fidOf(fid) {
        const state = fids.get(fid);
        need(state, ENOENT, "unknown fid " + fid);
        return state;
    }

    function validateName(name) {
        need(typeof name === "string" && name.length > 0, EINVAL, "empty name");
        need(name !== "." && name !== "..", EINVAL, "dot names are handled separately");
        need(!name.includes("/") && !name.includes("\\") && !name.includes("\0"), EINVAL, "invalid name");
    }

    /** Ребёнок пути: ".." поднимает вверх, но не выше корня (""). */
    function child(parent, name) {
        if (name === ".") return parent;
        if (name === "..") {
            const slash = parent.lastIndexOf("/");
            return slash < 0 ? "" : parent.slice(0, slash);
        }

        validateName(name);
        return parent === "" ? name : parent + "/" + name;
    }

    // ------------------------------------------------------------- каталоги

    function buildDirEntries(relative) {
        const list = fsApi.readdir(relative);
        const chunks = [];
        let position = 0;

        for (const entry of list) {
            const childPath = relative === "" ? entry.name : relative + "/" + entry.name;

            let stats;
            try { stats = fsApi.stat(childPath); }
            catch { continue; }

            const qid = qidOf(childPath, stats);
            const size = 13 + 8 + 1 + 2 + encoder.encode(entry.name).length;
            position += size;

            const writer = new Writer(size + 8);
            writer.qid(qid).u64(position).u8(stats.dir ? 4 : 8).str(entry.name);
            chunks.push(writer.toBuffer());
        }

        return { chunks, size: position };
    }

    // ------------------------------------------------------------- кадры

    function frame(type, tag, payload) {
        const writer = new Writer(payload.length + 7);
        writer.u32(payload.length + 7).u8(type).u16(tag).bytes(payload);
        return writer.toBuffer();
    }

    function reply(type, tag, payload) {
        return frame(type, tag, payload ? (payload.toBuffer ? payload.toBuffer() : payload) : new Uint8Array(0));
    }

    function replyError(tag, errno) {
        return frame(RLERROR, tag, new Writer(8).u32(errno).toBuffer());
    }

    // ------------------------------------------------------------- операции

    function version(reader, tag) {
        const msize = Math.min(reader.u32() || DEFAULT_MSIZE, DEFAULT_MSIZE);
        reader.str();

        return reply(RVERSION, tag, new Writer(64).u32(msize).str("9P2000.L"));
    }

    function attach(reader, tag) {
        const fid = reader.u32();
        reader.u32();
        reader.str();
        reader.str();
        reader.u32();

        const stats = fsApi.stat("");
        const qid = qidOf("", stats);

        fids.set(fid, { path: "", fd: null, dir: null, qid });
        return reply(RATTACH, tag, new Writer(16).qid(qid));
    }

    function walk(reader, tag) {
        const fid = reader.u32();
        const newfid = reader.u32();
        const count = reader.u16();

        const names = [];
        for (let i = 0; i < count; i++) names.push(reader.str());

        const state = fidOf(fid);

        let current = state.path;
        const qids = [];

        for (const name of names) {
            let next;

            try {
                next = child(current, name);
                const stats = fsApi.stat(next);
                qids.push(qidOf(next, stats));
            } catch (e) {
                if (qids.length === 0) throw e;
                break;
            }

            current = next;
        }

        const writer = new Writer(16 + qids.length * 15);
        writer.u16(qids.length);
        for (const qid of qids) writer.qid(qid);

        if (newfid !== fid) {
            fids.set(newfid, {
                path: current,
                fd: null,
                dir: null,
                qid: qids.length ? qids[qids.length - 1] : state.qid
            });
        } else if (qids.length) {
            state.path = current;
            state.qid = qids[qids.length - 1];
        }

        return reply(RWALK, tag, writer);
    }

    function lopen(reader, tag) {
        const fid = reader.u32();
        reader.u32();           // flags: чтение/запись решаем по операции

        const state = fidOf(fid);
        const stats = fsApi.stat(state.path);

        if (stats.dir) {
            state.dir = buildDirEntries(state.path);
        } else {
            state.dir = null;
        }

        state.qid = qidOf(state.path, stats);

        return reply(RLOPEN, tag, new Writer(24).qid(state.qid).u32(DEFAULT_MSIZE - 24));
    }

    function lcreate(reader, tag) {
        requireWritable();

        const fid = reader.u32();
        const name = reader.str();
        const mode = reader.u32();

        const parent = fidOf(fid);
        const target = child(parent.path, name);

        fsApi.create(target, (mode & 0o777) || 0o666);

        const stats = fsApi.stat(target);
        parent.path = target;
        parent.dir = null;
        parent.qid = qidOf(target, stats);

        return reply(RLCREATE, tag, new Writer(24).qid(parent.qid).u32(DEFAULT_MSIZE - 24));
    }

    function read(reader, tag) {
        const fid = reader.u32();
        const offset = reader.u64();
        const count = reader.u32();

        const state = fidOf(fid);
        const max = Math.min(count, DEFAULT_MSIZE - 11);

        if (state.dir) {
            const data = sliceDir(state.dir, offset, max);
            return reply(RREAD, tag, new Writer(data.length + 8).u32(data.length).bytes(data));
        }

        const data = fsApi.read(state.path, offset, max);
        return reply(RREAD, tag, new Writer(data.length + 8).u32(data.length).bytes(data));
    }

    function write(reader, tag) {
        requireWritable();

        const fid = reader.u32();
        const offset = reader.u64();
        const count = reader.u32();
        const data = reader.rest();

        need(data.length === count, EINVAL, "short write");

        const state = fidOf(fid);
        need(!state.dir, EISDIR, "is a directory");

        const written = fsApi.write(state.path, offset, data);

        return reply(RWRITE, tag, new Writer(8).u32(written));
    }

    function clunk(reader, tag) {
        const fid = reader.u32();
        fids.delete(fid);
        return reply(RCLUNK, tag);
    }

    function remove(reader, tag) {
        requireWritable();

        const fid = reader.u32();
        const state = fidOf(fid);

        fids.delete(fid);

        const stats = fsApi.stat(state.path);
        fsApi.remove(state.path, stats.dir);

        return reply(RREMOVE, tag);
    }

    function readlink(reader, tag) {
        const fid = reader.u32();
        const state = fidOf(fid);

        const target = fsApi.readlink(state.path);
        if (target === null || target === undefined) throw error(EINVAL, "not a symlink");

        return reply(RREADLINK, tag, new Writer(64).str(target));
    }

    function getattr(reader, tag) {
        const fid = reader.u32();
        reader.u64();

        const state = fidOf(fid);
        const stats = fsApi.stat(state.path);

        const mode = (stats.dir ? S_IFDIR : S_IFREG) | ((stats.mode || 0o666) & 0o777);

        const writer = new Writer(160);
        writer.u64(0x1FFF);
        writer.qid(qidOf(state.path, stats));
        writer.u32(mode);
        writer.u32(0);
        writer.u32(0);
        writer.u64(stats.nlink || 1);
        writer.u64(0);
        writer.u64(stats.size || 0);
        writer.u64(4096);
        writer.u64(Math.floor((stats.size || 0) / 512) + 1);
        writer.u64(Math.floor((stats.atimeMs || 0) / 1000)).u64(0);
        writer.u64(Math.floor((stats.mtimeMs || 0) / 1000)).u64(0);
        writer.u64(Math.floor((stats.ctimeMs || 0) / 1000)).u64(0);
        writer.u64(0).u64(0);
        writer.u64(0);
        writer.u64(0);

        return reply(RGETATTR, tag, writer);
    }

    function setattr(reader, tag) {
        const fid = reader.u32();
        const valid = reader.u32();

        const mode = reader.u32();
        reader.u32();
        reader.u32();
        const size = reader.u64();

        reader.u64(); reader.u64();
        reader.u64(); reader.u64();

        const state = fidOf(fid);

        if (valid & 0x8) {
            requireWritable();
            fsApi.truncate(state.path, size);
        }

        if (valid & 0x1) {
            requireWritable();
            fsApi.chmod(state.path, mode & 0o777);
        }

        return reply(RSETATTR, tag);
    }

    function readdir(reader, tag) {
        const fid = reader.u32();
        const offset = reader.u64();
        const count = reader.u32();

        const state = fidOf(fid);
        need(state.dir, ENOTDIR, "not a directory");

        const max = Math.min(count, DEFAULT_MSIZE - 11);
        const data = sliceDir(state.dir, offset, max);

        return reply(RREADDIR, tag, new Writer(data.length + 8).u32(data.length).bytes(data));
    }

    function sliceDir(dir, offset, max) {
        const parts = [];
        let position = 0;
        let total = 0;

        for (const chunk of dir.chunks) {
            const start = position;
            position += chunk.length;

            if (start < offset) continue;
            if (total + chunk.length > max) break;

            parts.push(chunk);
            total += chunk.length;
        }

        const result = new Uint8Array(total);
        let at = 0;

        for (const part of parts) {
            result.set(part, at);
            at += part.length;
        }

        return result;
    }

    function fsync(reader, tag) {
        const fid = reader.u32();
        reader.u32();

        const state = fidOf(fid);
        fsApi.fsync(state.path);

        return reply(RFSYNC, tag);
    }

    function statfs(reader, tag) {
        reader.u32();

        const stats = fsApi.statfs() || {};

        const writer = new Writer(64);
        writer.u32(0);
        writer.u32(stats.bsize || 4096);
        writer.u64(stats.blocks || 0);
        writer.u64(stats.bfree || 0);
        writer.u64(stats.bavail || 0);
        writer.u64(stats.files || 0);
        writer.u64(stats.ffree || 0);
        writer.u64(0);
        writer.u32(255);

        return reply(RSTATFS, tag, writer);
    }

    function mkdir(reader, tag) {
        requireWritable();

        const fid = reader.u32();
        const name = reader.str();
        const mode = reader.u32();
        reader.u32();

        const parent = fidOf(fid);
        const target = child(parent.path, name);

        fsApi.mkdir(target, (mode & 0o777) || 0o777);

        const stats = fsApi.stat(target);
        return reply(RMKDIR, tag, new Writer(16).qid(qidOf(target, stats)));
    }

    function renameat(reader, tag) {
        requireWritable();

        const oldFid = reader.u32();
        const oldName = reader.str();
        const newFid = reader.u32();
        const newName = reader.str();

        const from = child(fidOf(oldFid).path, oldName);
        const to = child(fidOf(newFid).path, newName);

        fsApi.rename(from, to);

        return reply(RRENAMEAT, tag);
    }

    function unlinkat(reader, tag) {
        requireWritable();

        const fid = reader.u32();
        const name = reader.str();
        const flags = reader.u32();

        const target = child(fidOf(fid).path, name);
        const stats = fsApi.stat(target);

        if (stats.dir || (flags & AT_REMOVEDIR)) {
            need(stats.dir, ENOTDIR, "not a directory");
            fsApi.remove(target, true);
        } else {
            fsApi.remove(target, false);
        }

        return reply(RUNLINKAT, tag);
    }

    // ------------------------------------------------------------- разбор

    function handle(request) {
        const reader = new Reader(request);

        reader.u32();
        const type = reader.u8();
        const tag = reader.u16();

        try {
            switch (type) {
                case TVERSION: return version(reader, tag);
                case TAUTH: return replyError(tag, EOPNOTSUPP);
                case TATTACH: return attach(reader, tag);
                case TFLUSH: return reply(RFLUSH, tag);
                case TWALK: return walk(reader, tag);
                case TREAD: return read(reader, tag);
                case TWRITE: return write(reader, tag);
                case TCLUNK: return clunk(reader, tag);
                case TREMOVE: return remove(reader, tag);
                case TLOPEN: return lopen(reader, tag);
                case TLCREATE: return lcreate(reader, tag);
                case TREADLINK: return readlink(reader, tag);
                case TGETATTR: return getattr(reader, tag);
                case TSETATTR: return setattr(reader, tag);
                case TREADDIR: return readdir(reader, tag);
                case TFSYNC: return fsync(reader, tag);
                case TSTATFS: return statfs(reader, tag);
                case TMKDIR: return mkdir(reader, tag);
                case TRENAMEAT: return renameat(reader, tag);
                case TUNLINKAT: return unlinkat(reader, tag);
                case TLINK:
                case TSYMLINK:
                case TLOCK:
                case TGETLOCK: return replyError(tag, EOPNOTSUPP);
                default:
                    log("9p: неизвестный тип " + type);
                    return replyError(tag, EOPNOTSUPP);
            }
        } catch (e) {
            const errno = errnoOf(e);
            log("9p: тип " + type + " -> errno " + errno + " (" + (e && e.message) + ")");
            return replyError(tag, errno);
        }
    }

    return function handle9p(request, replyBuffer) {
        let response;

        try {
            response = handle(request);
        } catch (e) {
            const tag = request.length >= 7 ? (request[5] | (request[6] << 8)) : 0;
            response = replyError(tag, errnoOf(e));
        }

        replyBuffer(response);
    };
}