// Быстрая проверка файлов old-dos.ru по HTTP Range: что внутри, не скачивая всё.
//
// Запуск: node tools/old-dos-scan.mjs <files.json> <out.json> [type-filter]
//
// Сайт отдаёт `Accept-Ranges` и `206`: достаточно первых 32 КБ (сигнатура и
// ISO-описатель на 0x8001) и, для ZIP, последних 128 КБ (центральный каталог с
// именами и размерами). Гигабайтный RAR так распознаётся за один запрос.
import fs from "node:fs";

const BASE = "http://old-dos.ru";
const [, , filesPath, outPath, typeFilter] = process.argv;

if (!filesPath || !outPath) {
    console.error("usage: node tools/old-dos-scan.mjs <files.json> <out.json> [filter]");
    process.exit(2);
}

const inventory = JSON.parse(fs.readFileSync(filesPath, "utf8"));

const GOOD = ["Образ CD", "Флоппи-версия", "LiveCD", "Идеальная копия"];

const FLOPPY = {
    163840: "160K", 184320: "180K", 327680: "320K", 368640: "360K",
    737280: "720K", 1228800: "1.2M", 1474560: "1.44M", 2949120: "2.88M"
};

function sleep(ms) { return new Promise((r) => setTimeout(r, ms)); }

async function range(url, header) {
    for (let attempt = 1; attempt <= 3; attempt++) {
        try {
            const response = await fetch(url, {
                headers: {
                    Range: header,
                    "User-Agent": "hv86/0.1.0 (+https://github.com/dpteam/hv86)"
                }
            });

            if (response.status !== 206 && response.status !== 200)
                throw new Error("HTTP " + response.status);

            return {
                buffer: Buffer.from(await response.arrayBuffer()),
                total: Number((response.headers.get("content-range") || "/0").split("/")[1] || 0),
                status: response.status
            };
        } catch (e) {
            if (attempt === 3) throw e;
            await sleep(1200 * attempt);
        }
    }
}

/** Имена и размеры из центрального каталога ZIP. */
function zipEntries(buffer) {
    const end = buffer.lastIndexOf(Buffer.from([0x50, 0x4b, 0x05, 0x06]));
    if (end < 0) return null;

    const count = buffer.readUInt16LE(end + 10);

    // В EOCD лежит АБСОЛЮТНОЕ смещение каталога в файле, а buffer - только
    // хвост. Раньше смещение использовалось как есть, и для архивов больше
    // хвоста каталог "не читался": наборы дискет выглядели как "no image
    // inside". Каталог всегда кончается там, где начинается EOCD.
    const cdSize = buffer.readUInt32LE(end + 12);
    let offset = end - cdSize;

    if (offset < 0) return null;

    const entries = [];

    for (let i = 0; i < count && offset + 46 <= buffer.length; i++) {
        if (buffer.readUInt32LE(offset) !== 0x02014b50) break;

        const size = buffer.readUInt32LE(offset + 24);
        const nameLength = buffer.readUInt16LE(offset + 28);
        const extraLength = buffer.readUInt16LE(offset + 30);
        const commentLength = buffer.readUInt16LE(offset + 32);
        const name = buffer.toString("latin1", offset + 46, offset + 46 + nameLength);

        entries.push({ name, size });
        offset += 46 + nameLength + extraLength + commentLength;
    }

    return entries;
}

const isImage = (name) => /\.(img|iso|ima|flp|vfd|dsk|raw|bin|hdd)$/i.test(name);

function kindOf(name, size) {
    if (/\.iso$/i.test(name)) return "cdrom";
    if (FLOPPY[size]) return "fda";
    if (size >= 2 * 1024 * 1024) return "hda";
    return "";
}

const wanted = typeFilter ? [typeFilter] : GOOD;

const candidates = inventory.files.filter((f) =>
    wanted.some((good) => (f.type || "").includes(good)));

console.log("candidates: " + candidates.length);

const result = [];
let done = 0;

for (const file of candidates) {
    const record = {
        download: file.download,
        entry: file.entry,
        entryName: file.entryName,
        type: file.type,
        declaredSize: file.size,
        version: (file.cells && file.cells[3]) || "",
        language: (file.cells && file.cells[4]) || "",
        quality: file.quality || "",
        bytes: 0,
        format: "",
        verdict: "",
        kind: "",
        image: null
    };

    try {
        const head = await range(file.url, "bytes=0-32783");
        record.bytes = head.total || head.buffer.length;

        const b = head.buffer;

        if (b[0] === 0x50 && b[1] === 0x4b) record.format = "zip";
        else if (b[0] === 0x52 && b[1] === 0x61 && b[2] === 0x72) record.format = "rar";
        else if (b[0] === 0x37 && b[1] === 0x7a) record.format = "7z";
        else if (b.length > 0x8005 &&
                 b[0x8001] === 0x43 && b[0x8002] === 0x44 && b[0x8003] === 0x30 &&
                 b[0x8004] === 0x30 && b[0x8005] === 0x31) record.format = "iso";
        else record.format = "raw";

        if (record.format === "iso") {
            record.image = { name: "iso", size: record.bytes };
            record.verdict = "usable";
            record.kind = "cdrom";
        }
        else if (record.format === "raw") {
            record.image = { name: "raw", size: record.bytes };
            record.verdict = "usable";
            record.kind = kindOf("raw.bin", record.bytes);
            if (!record.kind) record.verdict = "unknown media kind";
        }
        else if (record.format === "zip") {
            const tail = await range(file.url, "bytes=-131072");
            const entries = zipEntries(tail.buffer) || [];
            const images = entries.filter((e) => isImage(e.name) && e.size > 0);

            if (images.length === 1) {
                record.image = images[0];
                record.kind = kindOf(images[0].name, images[0].size);
                record.verdict = record.kind ? "usable" : "unknown media kind";
            }
            else if (images.length === 0) {
                record.verdict = "no image inside";
            }
            else {
                // Набор дискет (установочные диски DOS): в архиве несколько
                // образов. Годится, только если ВСЕ они - дискеты. Набор из
                // образов разных типов (или жёстких дисков) - не наш случай.
                const kinds = images.map((e) => kindOf(e.name, e.size));

                if (kinds.every((k) => k === "fda")) {
                    record.verdict = "usable-set";
                    record.kind = "fda";
                    record.images = images;
                } else {
                    record.verdict = "set: " + images.length + " images (" +
                        Array.from(new Set(kinds.map((k) => k || "?"))).join("+") + ")";
                }
            }
        }
        else {
            record.verdict = "unsupported archive (" + record.format + ")";
        }
    } catch (e) {
        record.verdict = "error: " + e.message;
    }

    result.push(record);
    done++;

    if (done % 50 === 0) console.log("  scanned: " + done + " / " + candidates.length);

    await sleep(120);
}

fs.writeFileSync(outPath, JSON.stringify(result, null, 1), "utf8");

const usable = result.filter((r) => r.verdict === "usable" || r.verdict === "usable-set");
const formats = new Map();
for (const r of result) formats.set(r.format, (formats.get(r.format) || 0) + 1);

console.log("");
console.log("formats: " + [...formats].map(([f, c]) => f + "=" + c).join(", "));
console.log("usable: " + usable.length + " of " + result.length +
    " (наборов дискет: " + result.filter((r) => r.verdict === "usable-set").length + ")");

for (const r of usable) {
    const size = r.verdict === "usable-set"
        ? (r.images || []).reduce((sum, e) => sum + e.size, 0) + " in " + r.images.length
        : Math.round((r.image ? r.image.size : r.bytes) / 1024) + "K";

    console.log("  " + String(r.download).padStart(7) + "  " + r.kind.padEnd(6) +
        String(size).padStart(9) + "  " + r.entryName.slice(0, 40));
}