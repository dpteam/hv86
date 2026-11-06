// Разбирает список образов из build/v86_all.js (сайт copy.sh/v86) в JSON.
//
// Запуск: node tools/copy-sh-list.mjs <v86_all.js> <список.json>
//
// Зачем: каталог сайта лежит в минифицированном бандле (там же, где эмулятор),
// поэтому вытаскиваем массив образов и разворачиваем минифицированные имена
// медиа в обычные. Имена взяты из кода, который собирает опции v86:
//   hda -> f.Y, hdb -> f.Cc, cdrom -> f.ca, fda -> f.ea, fdb -> f.lc,
//   multiboot -> f.Jc, bzimage -> f.hc, initrd -> f.Ec.
import fs from "node:fs";

const [, , input, output] = process.argv;

if (!input) {
    console.error("usage: node tools/copy-sh-list.mjs <v86_all.js> <out.json>");
    process.exit(2);
}

const text = fs.readFileSync(input, "utf8");
const start = text.indexOf('[{id:"archlinux"');

if (start < 0) {
    console.error("image list not found");
    process.exit(1);
}

// ищем закрывающую скобку массива, уважая строки
let depth = 0, inString = null, escaped = false, end = -1;

for (let i = start; i < text.length; i++) {
    const c = text[i];

    if (inString) {
        if (escaped) escaped = false;
        else if (c === "\\") escaped = true;
        else if (c === inString) inString = null;
        continue;
    }

    if (c === '"' || c === "'" || c === "`") { inString = c; continue; }
    if (c === "[") depth++;
    if (c === "]") { depth--; if (depth === 0) { end = i + 1; break; } }
}

if (end < 0) {
    console.error("array end not found");
    process.exit(1);
}

const source = text.slice(start, end);

// песочница: `d` - префикс CDN из сайта, прочие неизвестные - заглушки
const sandbox = new Proxy({ d: "//c.copy.sh/" }, {
    has: () => true,
    get: (target, key) => {
        if (key === Symbol.unscopables) return undefined;
        if (key in target) return target[key];
        if (key === "undefined") return undefined;
        if (key in globalThis) return globalThis[key];
        return () => "stub";
    }
});

let list;

try {
    list = new Function("sandbox", "with (sandbox) { return (" + source + "); }")(sandbox);
} catch (e) {
    console.error("eval failed: " + e.message);
    process.exit(1);
}

const MEDIA = { fda: "ea", fdb: "lc", hda: "Y", hdb: "Cc", cdrom: "ca" };

const result = list.map((item) => {
    const media = {};

    for (const [kind, key] of Object.entries(MEDIA)) {
        const value = item[key];
        if (value && typeof value === "object" && typeof value.url === "string") media[kind] = value.url;
    }

    return {
        id: item.id || "",
        name: item.name || "",
        memory_mb: Math.round((item.K || 0) / 1048576),
        vga_mb: Math.round((item.La || 0) / 1048576),
        net: item.bc || "",
        cmdline: item.ic || "",
        media,
        state: item.state && item.state.url ? item.state.url : "",
        has_filesystem: !!item.filesystem,
        bzimage: !!item.hc,
        initrd: !!item.Ec,
        multiboot: !!item.Jc,
        from_filesystem: !!item.Zf
    };
});

fs.writeFileSync(output, JSON.stringify(result, null, 1), "utf8");

// то, что мы реально умеем: одно медиа, без state, без 9p-корня, без bzimage
const usable = result.filter((r) => !r.state && !r.has_filesystem && !r.bzimage && !r.initrd &&
    !r.multiboot && !r.from_filesystem && Object.keys(r.media).length === 1);

console.log("entries: " + result.length);
console.log("usable (одно медиа, без state/9p/bzimage): " + usable.length);

for (const item of usable) {
    const kind = Object.keys(item.media)[0];
    console.log("  " + item.id.padEnd(22) + kind.padEnd(7) +
        String(item.memory_mb).padStart(4) + "MB  " + item.media[kind]);
}