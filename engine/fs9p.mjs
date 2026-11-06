/*
 * hv86 - 9p-сервер над настоящей папкой хоста (протокол 9P2000.L).
 *
 * v86 отдаёт нам сырые 9p-кадры (filesystem.handle9p) и ждёт готовый ответ,
 * поэтому здесь реализован сервер, а не прокси. Гость монтирует шару так:
 *
 *   mount -t 9p -o trans=virtio,version=9p2000.L host9p /mnt/share
 *
 * Безопасность: все пути разрешаются относительно корня шары, выход за него
 * (в том числе через ".." и симлинки) запрещён. Симлинки разыменовываются
 * и проверяются по realpath, поэтому «ссылка на C:\Windows» не сработает.
 */
import fs from "node:fs";
import path from "node:path";

// ------------------------------------------------------------------ константы

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
const TLINK = 70, RLINK = 71;
const TSYMLINK = 16, RSYMLINK = 17;
const TGETLOCK = 54, RGETLOCK = 55;
const TLOCK = 52, RLOCK = 53;
const RLERROR = 7;

const QTDIR = 0x80;
const S_IFDIR = 0x4000;
const S_IFREG = 0x8000;

const ENOENT = 2, EIO = 5, EACCES = 13, EEXIST = 17, ENOTDIR = 20, EISDIR = 21;
const EINVAL = 22, ENOTEMPTY = 39, EROFS = 30, EOPNOTSUPP = 95, ENOSYS = 38;

const DEFAULT_MSIZE = 8192;
const O_TRUNC = 0x200;
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

    u64() {
        const v = this.view.getBigUint64(this.offset, true);
        this.offset += 8;
        return Number(v);
    }

    str() {
        const length = this.u16();
        const bytes = this.buffer.subarray(this.offset, this.offset + length);
        this.offset += length;
        return Buffer.from(bytes).toString("utf8");
    }

    rest() {
        const bytes = this.buffer.subarray(this.offset);
        this.offset = this.buffer.length;
        return Buffer.from(bytes);
    }
}

class Writer {
    constructor(size = 512) {
        this.buffer = Buffer.alloc(size);
        this.offset = 0;
    }

    ensure(extra) {
        if (this.offset + extra <= this.buffer.length) return;

        let size = this.buffer.length * 2;
        while (size < this.offset + extra) size *= 2;

        const next = Buffer.alloc(size);
        this.buffer.copy(next, 0, 0, this.offset);
        this.buffer = next;
    }

    u8(value) { this.ensure(1); this.buffer.writeUInt8(value & 0xFF, this.offset); this.offset += 1; return this; }
    u16(value) { this.ensure(2); this.buffer.writeUInt16LE(value & 0xFFFF, this.offset); this.offset += 2; return this; }
    u32(value) { this.ensure(4); this.buffer.writeUInt32LE(value >>> 0, this.offset); this.offset += 4; return this; }

    u64(value) {
        this.ensure(8);
        const big = typeof value === "bigint" ? value : BigInt(Math.trunc(value));
        this.buffer.writeBigUInt64LE(big, this.offset);
        this.offset += 8;
        return this;
    }

    str(value) {
        const bytes = Buffer.from(value ?? "", "utf8");
        this.u16(bytes.length);
        this.ensure(bytes.length);
        bytes.copy(this.buffer, this.offset);
        this.offset += bytes.length;
        return this;
    }

    bytes(value) {
        this.ensure(value.length);
        Buffer.from(value).copy(this.buffer, this.offset);
        this.offset += value.length;
        return this;
    }

    qid(qid) {
        this.u8(qid.type);
        this.u32(qid.version);
        this.u64(qid.path);
        return this;
    }

    toBuffer() {
        return this.buffer.subarray(0, this.offset);
    }
}

// -------------------------------------------------------------------- сервер

export function createShareHandler(rootDir, options = {}) {
    const root = path.resolve(rootDir);

    // Windows отдаёт короткие имена (DARTPO~1) и разные регистры: сравнивать
    // «настоящие» пути можно только с настоящим корнем
    let rootReal = root;
    try { rootReal = fs.realpathSync.native ? fs.realpathSync.native(root) : fs.realpathSync(root); }
    catch { /* корень проверим позже */ }

    const readonly = !!options.readonly;
    const readOnlyMode = 0o555;
    const readWriteMode = 0o777;

    const fids = new Map();          // fid -> { path, fd, dir, qid }
    const qidPaths = new Map();      // относительный путь -> 64-битный qid.path
    let nextQid = 2;

    const log = options.log || (() => {});

    // ------------------------------------------------------------ утилиты

    function qidPathFor(relative) {
        const key = relative === "" ? "." : relative;

        if (qidPaths.has(key)) return qidPaths.get(key);

        // FNV-1a 64-bit: стабильный идентификатор, не зависящий от времени
        let hash = 0xcbf29ce484222325n;
        const bytes = Buffer.from(key.toLowerCase(), "utf8");

        for (const byte of bytes) {
            hash ^= BigInt(byte);
            hash = (hash * 0x100000001b3n) & 0xFFFFFFFFFFFFFFFFn;
        }

        if (hash === 0n) hash = BigInt(nextQid++);

        qidPaths.set(key, hash);
        return hash;
    }

    function relativeOf(absolute) {
        const relative = path.relative(root, absolute);
        return relative === "" ? "" : relative;
    }

    function isInside(absolute) {
        const resolved = path.resolve(absolute);
        return resolved === root || resolved.startsWith(root + path.sep);
    }

    /** Разрешает имя внутри каталога и не выпускает за корень. */
    function resolveChild(parent, name) {
        if (!name || name === ".") return parent;
        if (name.includes("/") || name.includes("\\") || name.includes("\0"))
            throw error(EINVAL, "invalid name in path");

        const candidate = path.resolve(parent, name);

        if (!isInside(candidate)) throw error(EACCES, "outside of the share");

        return candidate;
    }

    function statOf(absolute) {
        return fs.statSync(absolute);          // симлинки разыменовываются
    }

    function verifyRealPath(absolute) {
        // защита от симлинка, ведущего наружу
        try {
            const real = fs.realpathSync.native
                ? fs.realpathSync.native(absolute)
                : fs.realpathSync(absolute);

            const normalized = real.toLowerCase();
            const base = rootReal.toLowerCase();

            if (normalized !== base && !normalized.startsWith(base + path.sep)) {
                throw error(EACCES, "symlink escapes the share");
            }

            return real;
        } catch (e) {
            if (e && e.p9errno) throw e;
            throw error(ENOENT, "not found");
        }
    }

    function qidOf(absolute, stats) {
        const isDir = stats.isDirectory();
        return {
            type: isDir ? QTDIR : 0,
            version: 0,
            path: qidPathFor(relativeOf(absolute))
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

    function replyError(tag, errno) {
        return frame(RLERROR, tag, new Writer(8).u32(errno).toBuffer());
    }

    // ------------------------------------------------- сохранение/чтение файлов

    function openFile(state, flags) {
        if (state.fd !== null && state.fd !== undefined) return;

        const writable = (flags & 3) === 1 || (flags & 3) === 2;

        if (writable || (flags & O_TRUNC)) requireWritable();

        let mode = writable ? "r+" : "r";

        if (flags & O_TRUNC) mode = "w+";

        try {
            state.fd = fs.openSync(state.path, mode);
            state.writable = writable || !!(flags & O_TRUNC);
        } catch (e) {
            if (writable) {
                // r+ требует существующий файл; если просят только запись - создаём
                state.fd = fs.openSync(state.path, "w+");
                state.writable = true;
            } else {
                throw e;
            }
        }
    }

    function closeFid(state) {
        if (state && state.fd !== null && state.fd !== undefined) {
            try { fs.closeSync(state.fd); } catch { /* уже закрыт */ }
            state.fd = null;
        }
    }

    // ------------------------------------------------------------- каталоги

    function buildDirEntries(absolute) {
        const names = fs.readdirSync(absolute, { withFileTypes: true });
        const entries = [];

        for (const entry of names) {
            const child = path.resolve(absolute, entry.name);

            if (!isInside(child)) continue;

            let stats;

            try { stats = statOf(child); }
            catch { continue; }

            const qid = qidOf(child, stats);

            entries.push({
                qid,
                // 4 = DT_DIR, 8 = DT_REG (d_type из readdir)
                type: stats.isDirectory() ? 4 : 8,
                name: entry.name
            });
        }

        // каждый элемент: qid[13] offset[8] type[1] name[s]
        // offset - «непрозрачный» токен; используем байтовое смещение конца записи
        let position = 0;
        const chunks = [];

        for (const entry of entries) {
            const size = 13 + 8 + 1 + 2 + Buffer.byteLength(entry.name, "utf8");
            position += size;

            const writer = new Writer(size + 8);
            writer.qid(entry.qid).u64(position).u8(entry.type).str(entry.name);
            chunks.push(writer.toBuffer());
        }

        return { chunks, size: position };
    }

    // ------------------------------------------------------------- обработка

    function handle(request) {
        const reader = new Reader(request);

        const size = reader.u32();
        const type = reader.u8();
        const tag = reader.u16();

        if (size !== request.length) {
            log("9p: размер кадра " + size + " не совпал с " + request.length);
        }

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
                case TLINK: return replyError(tag, EOPNOTSUPP);
                case TSYMLINK: return replyError(tag, EOPNOTSUPP);
                case TLOCK:
                case TGETLOCK: return replyError(tag, EOPNOTSUPP);
                default:
                    log("9p: неизвестный тип " + type);
                    return replyError(tag, EOPNOTSUPP);
            }
        } catch (e) {
            const errno = errnoOf(e);
            log("9p: " + type + " -> errno " + errno + " (" + (e && e.message) + ")");
            return replyError(tag, errno);
        }
    }

    function frame(type, tag, payload) {
        const writer = new Writer(payload.length + 7);
        writer.u32(payload.length + 7).u8(type).u16(tag).bytes(payload);
        return writer.toBuffer();
    }

    function reply(type, tag, payload) {
        return frame(type, tag, payload ? payload.toBuffer ? payload.toBuffer() : payload : Buffer.alloc(0));
    }

    // ------------------------------------------------------------- операции

    function version(reader, tag) {
        const msize = Math.min(reader.u32() || DEFAULT_MSIZE, DEFAULT_MSIZE);
        const requested = reader.str();

        // поддерживаем только 9P2000.L (как и встроенный клиент v86)
        const versionName = requested.startsWith("9P2000.L") ? "9P2000.L" : "9P2000.L";

        return reply(RVERSION, tag, new Writer(64).u32(msize).str(versionName));
    }

    function attach(reader, tag) {
        const fid = reader.u32();
        reader.u32();          // afid
        reader.str();          // uname
        reader.str();          // aname
        reader.u32();          // n_uname

        const stats = statOf(root);

        fids.set(fid, {
            path: root,
            fd: null,
            dir: null,
            qid: qidOf(root, stats)
        });

        return reply(RATTACH, tag, new Writer(16).qid(qidOf(root, stats)));
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
                next = resolveChild(current, name);
                verifyRealPath(next);
                statOf(next);
            } catch (e) {
                if (qids.length === 0) throw e;
                break;
            }

            current = next;
            qids.push(qidOf(next, statOf(next)));
        }

        if (names.length > 0 && qids.length === 0) throw error(ENOENT, "walk failed");

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
        const flags = reader.u32();

        const state = fidOf(fid);
        const stats = statOf(state.path);

        if (stats.isDirectory()) {
            const list = buildDirEntries(state.path);
            state.dir = { chunks: list.chunks, size: list.size, opened: true };
            state.qid = qidOf(state.path, stats);
        } else {
            openFile(state, flags);
            state.qid = qidOf(state.path, stats);
        }

        return reply(RLOPEN, tag, new Writer(24).qid(state.qid).u32(DEFAULT_MSIZE - 24));
    }

    function lcreate(reader, tag) {
        requireWritable();

        const fid = reader.u32();
        const name = reader.str();
        const flags = reader.u32();
        const mode = reader.u32();
        reader.u32();          // gid

        const parent = fidOf(fid);
        const target = resolveChild(parent.path, name);

        need(!fs.existsSync(target), EEXIST, "file exists");

        const fd = fs.openSync(target, "w+");

        try { fs.chmodSync(target, (mode & 0o777) || 0o666); } catch { /* FAT */ }

        const stats = statOf(target);
        const qid = qidOf(target, stats);

        parent.path = target;
        parent.fd = fd;
        parent.dir = null;
        parent.qid = qid;

        if ((flags & 3) === 0) {
            // просили только чтение - переоткрываем
            try { fs.closeSync(fd); } catch { }
            parent.fd = fs.openSync(target, "r");
        }

        return reply(RLCREATE, tag, new Writer(24).qid(qid).u32(DEFAULT_MSIZE - 24));
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

        if (state.fd === null || state.fd === undefined) openFile(state, 0);

        const buffer = Buffer.alloc(max);
        const read = fs.readSync(state.fd, buffer, 0, max, offset);
        const data = buffer.subarray(0, read);

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

        if (state.fd === null || state.fd === undefined || !state.writable) {
            closeFid(state);
            openFile(state, 2);
        }

        const written = fs.writeSync(state.fd, data, 0, data.length, offset);

        return reply(RWRITE, tag, new Writer(8).u32(written));
    }

    function clunk(reader, tag) {
        const fid = reader.u32();
        const state = fids.get(fid);

        if (state) {
            closeFid(state);
            fids.delete(fid);
        }

        return reply(RCLUNK, tag);
    }

    function remove(reader, tag) {
        requireWritable();

        const fid = reader.u32();
        const state = fidOf(fid);

        closeFid(state);
        fids.delete(fid);

        const stats = statOf(state.path);

        if (stats.isDirectory()) fs.rmdirSync(state.path);
        else fs.unlinkSync(state.path);

        return reply(RREMOVE, tag);
    }

    function readlink(reader, tag) {
        const fid = reader.u32();
        const state = fidOf(fid);

        let target;

        try { target = fs.readlinkSync(state.path); }
        catch { return replyError(tag, EINVAL); }

        return reply(RREADLINK, tag, new Writer(64).str(target));
    }

    function getattr(reader, tag) {
        const fid = reader.u32();
        reader.u64();           // request_mask

        const state = fidOf(fid);
        const stats = statOf(state.path);

        const mode = (stats.isDirectory() ? S_IFDIR : S_IFREG) |
            (stats.mode & 0o777);

        const writer = new Writer(160);
        writer.u64(0x1FFF);                     // valid: всё, что отдаём
        writer.qid(qidOf(state.path, stats));
        writer.u32(mode);
        writer.u32(0);                          // uid
        writer.u32(0);                          // gid
        writer.u64(stats.nlink || 1);
        writer.u64(0);                          // rdev
        writer.u64(stats.size);
        writer.u64(4096);                       // blksize
        writer.u64(Math.floor(stats.size / 512) + 1);
        writer.u64(Math.floor(stats.atimeMs / 1000)).u64(0);
        writer.u64(Math.floor(stats.mtimeMs / 1000)).u64(0);
        writer.u64(Math.floor(stats.ctimeMs / 1000)).u64(0);
        writer.u64(0).u64(0);                   // btime
        writer.u64(0);                          // gen
        writer.u64(0);                          // data_version

        return reply(RGETATTR, tag, writer);
    }

    function setattr(reader, tag) {
        const fid = reader.u32();
        const valid = reader.u32();

        const mode = reader.u32();
        reader.u32();           // uid
        reader.u32();           // gid
        const size = reader.u64();

        reader.u64(); reader.u64();   // atime
        reader.u64(); reader.u64();   // mtime

        const state = fidOf(fid);

        if (valid & 0x8) {
            requireWritable();

            if (state.fd === null || state.fd === undefined) openFile(state, 1);
            fs.ftruncateSync(state.fd, size);
        }

        if (valid & 0x1) {
            requireWritable();
            try { fs.chmodSync(state.path, mode & 0o777); } catch { /* FAT */ }
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

    /** Возвращает целые записи каталога, начиная с байтового смещения. */
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

        return Buffer.concat(parts);
    }

    function fsync(reader, tag) {
        const fid = reader.u32();
        reader.u32();           // datasync

        const state = fidOf(fid);

        if (state.fd !== null && state.fd !== undefined) {
            try { fs.fsyncSync(state.fd); } catch { /* FAT */ }
        }

        return reply(RFSYNC, tag);
    }

    function statfs(reader, tag) {
        reader.u32();           // fid

        let blocks = 0, bfree = 0, bavail = 0, files = 0, ffree = 0, bsize = 4096;

        try {
            const stats = fs.statfsSync ? fs.statfsSync(root) : null;

            if (stats) {
                bsize = stats.bsize || 4096;
                blocks = stats.blocks || 0;
                bfree = stats.bfree || 0;
                bavail = stats.bavail || 0;
                files = stats.files || 0;
                ffree = stats.ffree || 0;
            }
        } catch { /* нет statfs - отдаём нули, Linux это переживёт */ }

        const writer = new Writer(64);
        writer.u32(0);           // type
        writer.u32(bsize);
        writer.u64(blocks);
        writer.u64(bfree);
        writer.u64(bavail);
        writer.u64(files);
        writer.u64(ffree);
        writer.u64(0);           // fsid
        writer.u32(255);         // namelen

        return reply(RSTATFS, tag, writer);
    }

    function mkdir(reader, tag) {
        requireWritable();

        const fid = reader.u32();
        const name = reader.str();
        const mode = reader.u32();
        reader.u32();           // gid

        const parent = fidOf(fid);
        const target = resolveChild(parent.path, name);

        fs.mkdirSync(target);

        try { fs.chmodSync(target, (mode & 0o777) || readWriteMode); } catch { /* FAT */ }

        const stats = statOf(target);
        return reply(RMKDIR, tag, new Writer(16).qid(qidOf(target, stats)));
    }

    function renameat(reader, tag) {
        requireWritable();

        const oldFid = reader.u32();
        const oldName = reader.str();
        const newFid = reader.u32();
        const newName = reader.str();

        const from = resolveChild(fidOf(oldFid).path, oldName);
        const to = resolveChild(fidOf(newFid).path, newName);

        fs.renameSync(from, to);

        return reply(RRENAMEAT, tag);
    }

    function unlinkat(reader, tag) {
        requireWritable();

        const fid = reader.u32();
        const name = reader.str();
        const flags = reader.u32();

        const target = resolveChild(fidOf(fid).path, name);
        const stats = statOf(target);

        if (stats.isDirectory() || (flags & AT_REMOVEDIR)) {
            need(stats.isDirectory(), ENOTDIR, "not a directory");
            fs.rmdirSync(target);
        } else {
            fs.unlinkSync(target);
        }

        return reply(RUNLINKAT, tag);
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