// Глубокий разбор RAR/7z из инвентаря old-dos.ru.
//
// Запуск:
//   node tools/old-dos-deep.mjs <candidates.json> <out.json> [--max-mb 40] [--only 3788,37210]
//
// Зачем отдельно от old-dos-scan.mjs: скан читает по HTTP Range только заголовок
// архива, а у RAR/7z список файлов так не достать - их надо скачать целиком и
// спросить у 7-Zip. Поэтому глубокий разбор включают точечно: для кандидатов,
// которые уже отобраны глазами, а не для всех 11 тысяч файлов.
//
// Нужен 7z.exe (тот же, что и приложению): PATH, %ProgramFiles%\7-Zip, или HV86_7Z.
// Выход - в формате old-dos-scan.mjs (verdict usable / usable-set / …), чтобы
// собранные записи можно было подмешать к общему скану перед сборкой каталога.
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { spawnSync } from "node:child_process";

const [, , inPath, outPath, ...flags] = process.argv;

if (!inPath || !outPath) {
    console.error("usage: node tools/old-dos-deep.mjs <candidates.json> <out.json> [--max-mb 40] [--only 1,2]");
    process.exit(2);
}

function flagValue(name, fallback) {
    const index = flags.indexOf(name);
    return index >= 0 && flags[index + 1] ? flags[index + 1] : fallback;
}

const maxMb = Number(flagValue("--max-mb", "40"));
const onlyList = flagValue("--only", "");
const only = onlyList ? new Set(onlyList.split(",").map((s) => s.trim())) : null;

const root = path.join(os.tmpdir(), "hv86-olddos-deep");
const cache = path.join(root, "cache");

fs.mkdirSync(cache, { recursive: true });

const FLOPPY = [163840, 184320, 327680, 368640, 737280, 1228800, 1474560, 2949120];
const isImage = (name) => /\.(img|iso|ima|flp|vfd|dsk|raw|bin|hdd)$/i.test(name);

function kindOf(name, size) {
    if (/\.iso$/i.test(name)) return "cdrom";
    if (FLOPPY.includes(size)) return "fda";
    // жёсткий диск: что-то осмысленное, но не стандартная дискета
    if (size >= 2 * 1024 * 1024) return "hda";
    return "";
}

function naturalCompare(a, b) {
    let i = 0, j = 0;

    while (i < a.length && j < b.length) {
        if (/\d/.test(a[i]) && /\d/.test(b[j])) {
            let si = i, sj = j;
            while (i < a.length && /\d/.test(a[i])) i++;
            while (j < b.length && /\d/.test(b[j])) j++;

            const va = Number(a.slice(si, i));
            const vb = Number(b.slice(sj, j));

            if (va !== vb) return va - vb;
            continue;
        }

        if (a[i] !== b[j]) return a[i].toLowerCase() < b[j].toLowerCase() ? -1 : 1;
        i++;
        j++;
    }

    return a.length - b.length;
}

/** Тот же поиск, что и в приложении (Vm/SevenZip.cs). */
function find7z() {
    const explicit = process.env.HV86_7Z;
    if (explicit && fs.existsSync(explicit)) return explicit;

    const names = ["7z.exe", "7za.exe", "7zr.exe"];
    const dirs = ["", process.env.ProgramFiles, process.env["ProgramFiles(x86)"], ...(process.env.PATH || "").split(";")];

    for (let dir of dirs) {
        if (!dir) continue;
        dir = dir.trim().replace(/^"|"$/g, "");
        if (!dir) continue;

        for (const name of names) {
            const candidate = path.join(dir, name);
            try { if (fs.existsSync(candidate)) return candidate; } catch { }
        }

        for (const name of names) {
            const candidate = path.join(dir, "7-Zip", name);
            try { if (fs.existsSync(candidate)) return candidate; } catch { }
        }
    }

    return null;
}

const sevenZip = find7z();

if (!sevenZip) {
    console.error("7-Zip не найден: укажите путь в HV86_7Z или установите 7-Zip");
    process.exit(3);
}

console.log("7-Zip: " + sevenZip);

/** Список файлов архива через `7z l -slt`. */
function listArchive(file) {
    const result = spawnSync(sevenZip, ["l", "-slt", "-sccUTF-8", file],
        { encoding: "utf8", windowsHide: true, maxBuffer: 64 * 1024 * 1024 });

    if (result.status > 1) return null;

    const entries = [];
    let current = null;
    let inList = false;

    for (const raw of (result.stdout || "").split(/\r?\n/)) {
        if (raw.startsWith("----------")) { inList = true; continue; }
        if (!inList) continue;

        if (raw.startsWith("Path = ")) {
            if (current) entries.push(current);
            current = { name: raw.slice(7), size: 0, folder: false };
        } else if (current && raw.startsWith("Size = ")) {
            current.size = Number(raw.slice(7).trim()) || 0;
        } else if (current && raw.startsWith("Folder = ")) {
            current.folder = raw.slice(9).trim() === "+";
        } else if (current && raw.startsWith("Attributes = ")) {
            if (raw.slice(13).trim().startsWith("D")) current.folder = true;
        }
    }

    if (current) entries.push(current);

    return entries;
}

const scan = JSON.parse(fs.readFileSync(inPath, "utf8"));

const candidates = scan.filter((r) =>
    (r.format === "rar" || r.format === "7z") &&
    r.bytes > 0 && r.bytes <= maxMb * 1024 * 1024 &&
    (!only || only.has(String(r.download))));

console.log("кандидатов: " + candidates.length + " (до " + maxMb + " МБ)");

const out = [];

for (const item of candidates) {
    const id = String(item.download);
    const archive = path.join(cache, id + "." + item.format);

    const record = {
        download: item.download,
        entry: item.entry,
        entryName: item.entryName,
        type: item.type,
        declaredSize: item.declaredSize,
        version: item.version,
        language: item.language,
        quality: item.quality,
        bytes: item.bytes,
        format: item.format,
        verdict: "",
        kind: "",
        images: []
    };

    try {
        if (!fs.existsSync(archive)) {
            const response = await fetch("http://old-dos.ru/dl.php?id=" + id, {
                headers: { "User-Agent": "hv86/0.1.0 (+https://github.com/dpteam/hv86)" }
            });

            if (!response.ok) throw new Error("HTTP " + response.status);

            fs.writeFileSync(archive, Buffer.from(await response.arrayBuffer()));
        }

        const entries = listArchive(archive);

        if (!entries) {
            record.verdict = "7-Zip не прочитал архив";
            out.push(record);
            console.log(String(item.download).padStart(6) + "  unreadable  " + item.entryName);
            continue;
        }

        const images = entries.filter((e) => !e.folder && isImage(e.name) && e.size > 0);
        images.sort((a, b) => naturalCompare(a.name, b.name));

        record.images = images.map((e) => ({ name: e.name, size: e.size }));

        if (images.length === 0) {
            record.verdict = "no image inside";
        } else if (images.length === 1) {
            record.kind = kindOf(images[0].name, images[0].size);
            record.image = images[0];
            record.verdict = record.kind ? "usable" : "unknown media kind";
        } else {
            const kinds = images.map((e) => kindOf(e.name, e.size));

            if (kinds.every((k) => k === "fda")) {
                record.kind = "fda";
                record.verdict = "usable-set";
            } else {
                record.verdict = "set: " + images.length + " images (" +
                    Array.from(new Set(kinds.map((k) => k || "?"))).join("+") + ")";
            }
        }

        console.log(String(item.download).padStart(6) + "  " + record.verdict.padEnd(24) + "  " +
            images.slice(0, 3).map((e) => e.name + " (" + Math.round(e.size / 1024) + "K)").join(", ") +
            (images.length > 3 ? " …" : ""));
    } catch (e) {
        record.verdict = "error: " + e.message;
        console.log(String(item.download).padStart(6) + "  error: " + e.message);
    }

    out.push(record);
}

fs.writeFileSync(outPath, JSON.stringify(out, null, 1), "utf8");

const summary = new Map();
for (const r of out) summary.set(r.verdict, (summary.get(r.verdict) || 0) + 1);

console.log("");
console.log("итог: " + [...summary].map(([k, v]) => k + "=" + v).join(", "));