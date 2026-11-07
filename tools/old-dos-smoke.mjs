// Boot-проверка КАНДИДАТОВ old-dos.ru (до попадания в каталог): скачивает
// архив, берёт ПЕРВЫЙ образ (дискету 1) и запускает `hv86 --smoke`.
//
// Запуск:
//   node tools/old-dos-smoke.mjs <candidates.json> <out.json> [--only 974,3850]
//
// candidates.json - выгрузка old-dos-scan.mjs (или её подмножество). Берутся
// записи с вердиктом usable и usable-set: у набора проверяется дискета 1,
// потому что именно с неё ВМ и стартует.
//
// Отличие от tools/old-dos-boot.ps1: тот проверяет уже собранные пресеты
// (id вида olddos-*) через headless-движок, а этот - сырые кандидаты, которых
// в каталоге ещё нет. Оба нужны: отбор идёт до сборки каталога.
//
// Почему отдельным шагом, а не в скане: скан смотрит только заголовок архива
// по HTTP Range, а загрузка проверяет, что образ реально доходит до гостя,
// читается BIOS и печатает что-то осмысленное (а не «Non-System disk»).
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { spawnSync } from "node:child_process";

const [, , inPath, outPath, ...flags] = process.argv;

if (!inPath || !outPath) {
    console.error("usage: node tools/old-dos-boot.mjs <candidates.json> <out.json> [--only id,id]");
    process.exit(2);
}

const onlyIndex = flags.indexOf("--only");
const only = onlyIndex >= 0 && flags[onlyIndex + 1]
    ? new Set(flags[onlyIndex + 1].split(",").map((s) => s.trim()))
    : null;

const root = process.env.HV86_ROOT_BOOT || path.join(os.tmpdir(), "hv86-olddos-boot");
const cache = path.join(root, "cache");
const work = path.join(root, "work");

fs.mkdirSync(cache, { recursive: true });
fs.mkdirSync(work, { recursive: true });

const scan = JSON.parse(fs.readFileSync(inPath, "utf8"));
const candidates = scan.filter((r) =>
    (r.verdict === "usable" || r.verdict === "usable-set") &&
    (!only || only.has(String(r.download))));

const isImage = (name) => /\.(img|iso|ima|flp|vfd|dsk|raw|bin|hdd)$/i.test(name);

/** Естественная сортировка: disk2 раньше disk10. */
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

/** Список образов из архива: `tar -tf` понимает zip начиная с Windows 10. */
function listArchive(file) {
    const result = spawnSync("tar", ["-tf", file], { encoding: "utf8", windowsHide: true });

    if (result.status !== 0) return null;

    return result.stdout.split(/\r?\n/).map((s) => s.trim()).filter(Boolean);
}

function walk(dir, out) {
    for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
        const full = path.join(dir, entry.name);

        if (entry.isDirectory()) walk(full, out);
        else out.push(full);
    }

    return out;
}

/**
 * Распаковывает архив целиком: вытащить один файл по имени нельзя - в именах
 * из old-dos.ru встречаются квадратные скобки (`[!]`), а bsdtar читает их как
 * маску и «не находит» файл.
 */
function extractAll(file, dir) {
    fs.mkdirSync(dir, { recursive: true });

    const result = spawnSync("tar", ["-xf", file, "-C", dir], { encoding: "utf8", windowsHide: true });

    if (result.status !== 0) return null;

    return walk(dir, []).filter((f) => isImage(path.basename(f)));
}

function screenFromLog(dir) {
    const logs = path.join(dir, "logs");

    if (!fs.existsSync(logs)) return "";

    for (const name of fs.readdirSync(logs)) {
        const text = fs.readFileSync(path.join(logs, name), "utf8");
        const lines = text.split(/\r?\n/).filter((l) => l.includes("[SMOKE]"));

        for (let i = lines.length - 1; i >= 0; i--) {
            const marker = lines[i].indexOf("экран:");
            if (marker >= 0) return lines[i].slice(marker + 6).trim();
        }
    }

    return "";
}

const out = [];

for (const item of candidates) {
    const id = String(item.download);
    const archive = path.join(cache, id + ".zip");

    const record = { download: item.download, name: item.entryName, screen: "", verdict: "", note: "" };

    try {
        if (!fs.existsSync(archive)) {
            const response = await fetch("http://old-dos.ru/dl.php?id=" + id, {
                headers: { "User-Agent": "hv86/0.1.0 (+https://github.com/dpteam/hv86)" }
            });

            if (!response.ok) throw new Error("HTTP " + response.status);

            fs.writeFileSync(archive, Buffer.from(await response.arrayBuffer()));
        }

        const entries = (listArchive(archive) || []).filter(isImage);

        if (entries.length === 0) {
            record.verdict = "no image";
            out.push(record);
            continue;
        }

        const unpacked = extractAll(archive, path.join(work, "unpack-" + id));

        if (!unpacked || unpacked.length === 0) throw new Error("не удалось распаковать архив");

        // первая дискета: именно с неё ВМ стартует
        unpacked.sort((a, b) => naturalCompare(path.basename(a), path.basename(b)));

        const target = unpacked[0];

        const smokeRoot = path.join(work, "smoke-" + id);

        try { fs.rmSync(smokeRoot, { recursive: true, force: true }); } catch { }

        const run = spawnSync("dotnet", [path.join("bin", "hv86.dll"), "--smoke", target], {
            encoding: "utf8",
            windowsHide: true,
            env: { ...process.env, HV86_SMOKE_ROOT: smokeRoot },
            timeout: 180000
        });

        record.screen = screenFromLog(smokeRoot);
        record.exitCode = run.status;
        record.firstImage = path.basename(target);

        // что осталось на экране после того, как убрали строки BIOS
        const guestText = record.screen
            .replace(/SeaBIOS \(version[^)]*\)/i, " ")
            .replace(/Booting from [A-Za-z]+\.\.\./i, " ")
            .trim();

        if (run.status !== 0) {
            record.verdict = "smoke failed";
            record.note = (run.stdout || "").split(/\r?\n/).filter(Boolean).slice(-2).join(" ");
        } else if (/non-system|no bootable|not a bootable|missing operating|operating system not found|invalid system disk/i.test(record.screen)) {
            record.verdict = "not bootable";
        } else if (guestText.length === 0) {
            record.verdict = "graphics or silent";
        } else if (guestText.length < 40) {
            record.verdict = "unclear (bios only)";
        } else {
            record.verdict = "boots";
        }

        console.log(String(item.download).padStart(6) + "  " + record.verdict.padEnd(20) +
            "  " + record.screen.slice(0, 70));
    } catch (e) {
        record.verdict = "error";
        record.note = e.message;
        console.log(String(item.download).padStart(6) + "  error: " + e.message);
    }

    out.push(record);
}

fs.writeFileSync(outPath, JSON.stringify(out, null, 1), "utf8");

const summary = new Map();
for (const r of out) summary.set(r.verdict, (summary.get(r.verdict) || 0) + 1);

console.log("");
console.log("итог: " + [...summary].map(([k, v]) => k + "=" + v).join(", "));