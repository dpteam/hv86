// Проверка кандидатов с old-dos.ru: что реально лежит внутри скачанного файла.
//
// Запуск:
//   node tools/old-dos-verify.mjs <candidates.json> <out.json>
//
// candidates.json: [{ "file": <dl.php id>, "entry": "<название>" }, ...]
//
// Для каждого: качаем (кэш в .old-dos-cache/dl), определяем формат по
// сигнатуре, для ZIP читаем список файлов из центрального каталога (zlib не
// нужен - размеры и имена лежат открыто) и решаем:
//   * один образ в архиве        -> usable, kind по размеру;
//   * ISO                        -> usable, kind=cdrom;
//   * несколько образов          -> набор дискет, не годится (гость попросит
//                                   следующую, а подставить её нечем);
//   * RAR/7z                     -> не поддерживаем;
//   * россыпь файлов без образа  -> не годится.
import fs from "node:fs";
import path from "node:path";
import crypto from "node:crypto";

const BASE = "http://old-dos.ru";
const CACHE = path.join(process.cwd(), ".old-dos-cache", "dl");

const [, , candidatesPath, outPath] = process.argv;

if (!candidatesPath || !outPath) {
    console.error("usage: node tools/old-dos-verify.mjs <candidates.json> <out.json>");
    process.exit(2);
}

fs.mkdirSync(CACHE, { recursive: true });

const FLOPPY = { 512: "boot sector", 163840: "160K", 184320: "180K", 327680: "320K", 368640: "360K", 737280: "720K", 1228800: "1.2M", 1474560: "1.44M", 2949120: "2.88M" };

function sleep(ms) { return new Promise((r) => setTimeout(r, ms)); }

async function download(id) {
    const file = path.join(CACHE, String(id) + ".bin");

    if (fs.existsSync(file)) return fs.readFileSync(file);

    for (let attempt = 1; attempt <= 3; attempt++) {
        try {
            const response = await fetch(BASE + "/dl.php?id=" + id, {
                headers: { "User-Agent": "hv86/0.1.0 (+https://github.com/dpteam/hv86)" }
            });

            if (!response.ok) throw new Error("HTTP " + response.status);

            const buffer = Buffer.from(await response.arrayBuffer());
            fs.writeFileSync(file, buffer);
            await sleep(250);

            return buffer;
        } catch (e) {
            if (attempt === 3) throw e;
            await sleep(1500 * attempt);
        }
    }
}

/** Формат по сигнатуре - расширению не верим. */
function detect(buffer) {
    if (buffer.length > 4 && buffer[0] === 0x50 && buffer[1] === 0x4b) return "zip";
    if (buffer.length > 4 && buffer[0] === 0x52 && buffer[1] === 0x61 && buffer[2] === 0x72) return "rar";
    if (buffer.length > 4 && buffer[0] === 0x37 && buffer[1] === 0x7a) return "7z";

    if (buffer.length > 0x8006 &&
        buffer[0x8001] === 0x43 && buffer[0x8002] === 0x44 &&
        buffer[0x8003] === 0x30 && buffer[0x8004] === 0x30 && buffer[0x8005] === 0x31) return "iso";

    return "raw";
}

/** Имена и размеры из центрального каталога ZIP (без распаковки). */
function zipEntries(buffer) {
    const end = buffer.lastIndexOf(Buffer.from([0x50, 0x4b, 0x05, 0x06]));
    if (end < 0) return [];

    const count = buffer.readUInt16LE(end + 10);
    let offset = buffer.readUInt32LE(end + 16);

    const entries = [];

    for (let i = 0; i < count && offset + 46 <= buffer.length; i++) {
        if (buffer.readUInt32LE(offset) !== 0x02014b50) break;

        const size = buffer.readUInt32LE(offset + 24);
        const nameLength = buffer.readUInt16LE(offset + 28);
        const extraLength = buffer.readUInt16LE(offset + 30);
        const commentLength = buffer.readUInt16LE(offset + 32);

        // имена в старых архивах в cp866/cp1251 - нам достаточно расширения
        const name = buffer.toString("latin1", offset + 46, offset + 46 + nameLength);

        entries.push({ name, size });
        offset += 46 + nameLength + extraLength + commentLength;
    }

    return entries;
}

const images = (entries) => entries.filter((e) =>
    /\.(img|iso|ima|flp|vfd|dsk|raw|bin|hdd)$/i.test(e.name) && e.size > 0);

function kindOf(file) {
    if (file.iso) return "cdrom";
    if (FLOPPY[file.size]) return "fda";
    if (file.size >= 2 * 1024 * 1024) return "hda";
    return "";
}

const candidates = JSON.parse(fs.readFileSync(candidatesPath, "utf8"));
const result = [];

for (const candidate of candidates) {
    const id = Number(candidate.file);
    let buffer;

    try {
        buffer = await download(id);
    } catch (e) {
        result.push({ file: id, entry: candidate.entry, verdict: "download failed", error: e.message });
        continue;
    }

    const format = detect(buffer);
    const sha256 = crypto.createHash("sha256").update(buffer).digest("hex");

    const record = {
        file: id,
        entry: candidate.entry,
        bytes: buffer.length,
        format,
        sha256,
        verdict: "",
        image: null
    };

    if (format === "rar" || format === "7z") {
        record.verdict = "unsupported archive (" + format + ")";
    }
    else if (format === "iso") {
        record.image = { name: "iso", size: buffer.length };
        record.verdict = "usable";
    }
    else if (format === "raw") {
        record.image = { name: "raw", size: buffer.length };
        record.verdict = "usable";
    }
    else {
        const list = images(zipEntries(buffer));

        if (list.length === 1) {
            record.image = list[0];
            record.verdict = "usable";
        }
        else if (list.length === 0) {
            record.verdict = "no image inside (loose files)";
        }
        else {
            record.verdict = "diskette set: " + list.length + " images";
            record.images = list.map((e) => e.name);
        }
    }

    if (record.verdict === "usable") {
        record.kind = kindOf({ ...record.image, iso: format === "iso" || /\.iso$/i.test(record.image.name) });
        if (!record.kind) record.verdict = "unknown media kind";
        record.floppy = FLOPPY[record.image.size] || "";
    }

    result.push(record);

    console.log(String(id).padStart(7) + "  " + format.padEnd(5) + "  " +
        String(Math.round(buffer.length / 1024)).padStart(7) + "K  " +
        record.verdict.padEnd(28) + "  " + (record.kind || record.floppy || ""));
}

fs.writeFileSync(outPath, JSON.stringify(result, null, 1), "utf8");

const usable = result.filter((r) => r.verdict === "usable");
console.log("");
console.log("usable: " + usable.length + " of " + result.length);