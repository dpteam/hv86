/*
 * hv86 - файловый адаптер общей папки для Node (headless-движок).
 *
 * Реализует ровно тот интерфейс, который ждёт engine/fs9p.mjs, и держит
 * все проверки безопасности хоста в одном месте:
 *   - относительный путь разбирается на компоненты и склеивается с корнем;
 *   - ".." и разделители в компонентах запрещены;
 *   - настоящий путь (после симлинков) обязан остаться внутри корня.
 */
import fs from "node:fs";
import path from "node:path";

function fail(code, message) {
    const e = new Error(message || code);
    e.code = code;
    return e;
}

export function createNodeFs(rootDir) {
    // корень сразу приводим к «настоящему» виду: на Windows путь из mkdtemp
    // может быть коротким (DARTPO~1), и тогда сравнение с реальными путями
    // файлов внутри ломается
    const initial = path.resolve(rootDir);

    let root = initial;
    try {
        root = fs.realpathSync.native ? fs.realpathSync.native(initial) : fs.realpathSync(initial);
    } catch {
        // корня ещё нет - пусть падают операции
    }

    const baseLower = root.toLowerCase();

    function inside(candidate) {
        const value = candidate.toLowerCase();
        return value === baseLower || value.startsWith(baseLower + path.sep);
    }

    /** Относительный 9p-путь -> настоящий путь на диске. */
    function host(relative) {
        if (!relative) return root;

        const parts = relative.split("/");

        for (const part of parts) {
            if (!part || part === "." || part === "..") throw fail("EACCES", "invalid path component");
            if (part.includes("\\") || part.includes("\0")) throw fail("EACCES", "invalid path component");
        }

        const full = path.resolve(root, ...parts);

        if (!inside(full)) throw fail("EACCES", "outside of the share");

        return full;
    }

    /** Проверка через realpath: симлинк не должен вывести из шары. */
    function verify(full) {
        let real;

        try {
            real = fs.realpathSync.native ? fs.realpathSync.native(full) : fs.realpathSync(full);
        } catch (e) {
            throw e;                            // ENOENT и компания - как есть
        }

        if (!inside(real)) throw fail("EACCES", "symlink escapes the share");

        return real;
    }

    function ensureParent(full) {
        const parent = path.dirname(full);
        if (!inside(path.resolve(parent))) throw fail("EACCES", "outside of the share");
        if (!fs.existsSync(parent)) throw fail("ENOENT", "parent directory does not exist");
    }

    return {
        stat(relative) {
            const full = host(relative);
            const real = verify(full);
            const stats = fs.statSync(real);

            return {
                dir: stats.isDirectory(),
                size: stats.size,
                atimeMs: stats.atimeMs,
                mtimeMs: stats.mtimeMs,
                ctimeMs: stats.ctimeMs,
                nlink: stats.nlink || 1,
                mode: stats.mode & 0o777
            };
        },

        readdir(relative) {
            const full = host(relative);
            const real = verify(full);

            return fs.readdirSync(real, { withFileTypes: true }).map((entry) => ({
                name: entry.name,
                dir: entry.isDirectory()
            }));
        },

        read(relative, offset, count) {
            const full = host(relative);
            const real = verify(full);

            const fd = fs.openSync(real, "r");

            try {
                const buffer = Buffer.alloc(count);
                const read = fs.readSync(fd, buffer, 0, count, offset);
                return buffer.subarray(0, read);
            } finally {
                try { fs.closeSync(fd); } catch { /* уже закрыт */ }
            }
        },

        write(relative, offset, data) {
            const full = host(relative);

            if (fs.existsSync(full)) verify(full);
            else ensureParent(full);

            const fd = fs.openSync(full, fs.existsSync(full) ? "r+" : "w+");

            try {
                return fs.writeSync(fd, data, 0, data.length, offset);
            } finally {
                try { fs.closeSync(fd); } catch { /* уже закрыт */ }
            }
        },

        create(relative, mode) {
            const full = host(relative);

            if (fs.existsSync(full)) throw fail("EEXIST", "file exists");

            ensureParent(full);

            const fd = fs.openSync(full, "w+");

            try {
                try { fs.chmodSync(full, mode); } catch { /* FAT */ }
            } finally {
                try { fs.closeSync(fd); } catch { /* уже закрыт */ }
            }
        },

        truncate(relative, size) {
            const full = host(relative);
            verify(full);

            fs.truncateSync(full, size);
        },

        mkdir(relative, mode) {
            const full = host(relative);

            if (fs.existsSync(full)) throw fail("EEXIST", "already exists");

            ensureParent(full);
            fs.mkdirSync(full);

            try { fs.chmodSync(full, mode); } catch { /* FAT */ }
        },

        remove(relative, isDir) {
            const full = host(relative);
            verify(full);

            if (isDir) fs.rmdirSync(full);
            else fs.unlinkSync(full);
        },

        rename(fromRelative, toRelative) {
            const from = host(fromRelative);
            const to = host(toRelative);

            verify(from);
            ensureParent(to);

            fs.renameSync(from, to);
        },

        readlink(relative) {
            const full = host(relative);

            try { return fs.readlinkSync(full); }
            catch { return null; }
        },

        chmod(relative, mode) {
            const full = host(relative);
            verify(full);

            try { fs.chmodSync(full, mode); } catch { /* FAT */ }
        },

        fsync(relative) {
            const full = host(relative);
            const real = verify(full);

            let fd = null;

            try {
                fd = fs.openSync(real, "r+");
                fs.fsyncSync(fd);
            } catch {
                // только чтение / FAT - не повод рвать операцию
            } finally {
                if (fd !== null) {
                    try { fs.closeSync(fd); } catch { /* уже закрыт */ }
                }
            }
        },

        statfs() {
            try {
                const stats = fs.statfsSync(root);

                return {
                    bsize: stats.bsize || 4096,
                    blocks: stats.blocks || 0,
                    bfree: stats.bfree || 0,
                    bavail: stats.bavail || 0,
                    files: stats.files || 0,
                    ffree: stats.ffree || 0
                };
            } catch {
                return null;
            }
        }
    };
}