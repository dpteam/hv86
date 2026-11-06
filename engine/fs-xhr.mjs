/*
 * hv86 - файловый адаптер общей папки для web-движка.
 *
 * В браузере нет fs, а `handle9p` у v86 синхронный, поэтому каждая
 * файловая операция - синхронный POST к нашему же ресурсному серверу
 * (`/share`). Хост-пути и все проверки «не выйти наружу» живут на стороне
 * C# (ShareFileServer), страница присылает только относительные имена.
 */
export function createXhrFs(endpoint) {
    function call(op, args) {
        const xhr = new XMLHttpRequest();
        xhr.open("POST", endpoint, false);          // именно синхронно: handle9p
        xhr.setRequestHeader("Content-Type", "application/json");
        xhr.send(JSON.stringify(Object.assign({ op: op }, args || {})));

        let response;

        try { response = JSON.parse(xhr.responseText || "{}"); }
        catch { throw withCode("EIO", "bad response from /share"); }

        if (response && response.error) throw withCode(response.error, response.error);

        return response || {};
    }

    function withCode(code, message) {
        const e = new Error(message || code);
        e.code = code;
        return e;
    }

    function toBase64(bytes) {
        let binary = "";
        const chunk = 0x8000;

        for (let i = 0; i < bytes.length; i += chunk)
            binary += String.fromCharCode.apply(null, bytes.subarray(i, Math.min(i + chunk, bytes.length)));

        return btoa(binary);
    }

    function fromBase64(text) {
        const binary = atob(text || "");
        const bytes = new Uint8Array(binary.length);

        for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);

        return bytes;
    }

    return {
        stat(relative) {
            return call("stat", { path: relative });
        },

        readdir(relative) {
            const result = call("readdir", { path: relative });
            return result.entries || [];
        },

        read(relative, offset, count) {
            const result = call("read", { path: relative, offset: offset, count: count });
            return fromBase64(result.data);
        },

        write(relative, offset, data) {
            const result = call("write", {
                path: relative,
                offset: offset,
                data: toBase64(data)
            });

            return result.written || 0;
        },

        create(relative, mode) {
            call("create", { path: relative, mode: mode });
        },

        truncate(relative, size) {
            call("truncate", { path: relative, size: size });
        },

        mkdir(relative, mode) {
            call("mkdir", { path: relative, mode: mode });
        },

        remove(relative, isDir) {
            call("remove", { path: relative, dir: !!isDir });
        },

        rename(fromRelative, toRelative) {
            call("rename", { from: fromRelative, to: toRelative });
        },

        readlink(relative) {
            const result = call("readlink", { path: relative });
            return result.target === undefined ? null : result.target;
        },

        chmod(relative, mode) {
            call("chmod", { path: relative, mode: mode });
        },

        fsync(relative) {
            call("fsync", { path: relative });
        },

        statfs() {
            const result = call("statfs", {});
            return {
                bsize: result.bsize,
                blocks: result.blocks,
                bfree: result.bfree,
                bavail: result.bavail,
                files: result.files,
                ffree: result.ffree
            };
        }
    };
}