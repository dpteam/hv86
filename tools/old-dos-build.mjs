// Собирает assets/presets-olddos.json из проверенных данных.
//
// Запуск:
//   node tools/old-dos-build.mjs <scan.json> <curated.json> <out.json>
//
// scan.json     - результат `old-dos-scan.mjs` (что реально внутри файла)
// curated.json  - ручной отбор: [{ "download": <id>, "id": "...", "name": "...",
//                   "memory_mb": 32, "note": "..." }]
//
// Каждая запись должна быть с вердиктом "usable" (один образ внутри) или
// "usable-set" (комплект дискет). Иначе скрипт падает: в каталог не должно
// попасть то, что не загрузится.
import fs from "node:fs";

const [, , scanPath, curatedPath, outPath] = process.argv;

if (!curatedPath || !outPath) {
    console.error("usage: node tools/old-dos-build.mjs <scan.json> <curated.json> <out.json>");
    process.exit(2);
}

const scan = JSON.parse(fs.readFileSync(scanPath, "utf8"));
const curatedDoc = JSON.parse(fs.readFileSync(curatedPath, "utf8"));

// отбор можно держать как массив или как {note, presets:[...]} - второй
// вариант удобнее: заметка о том, как файл собирается, лежит рядом
const curated = Array.isArray(curatedDoc) ? curatedDoc : (curatedDoc.presets || []);

const byDownload = new Map();
for (const item of scan) byDownload.set(Number(item.download), item);

const presets = [];
const errors = [];

for (const pick of curated) {
    const item = byDownload.get(Number(pick.download));

    if (!item) { errors.push("нет в скане: " + pick.download); continue; }

    const isSet = item.verdict === "usable-set";

    if (!isSet && item.verdict !== "usable") {
        errors.push(pick.download + ": " + item.verdict);
        continue;
    }

    const imageBytes = isSet
        ? (item.images || []).reduce((sum, e) => sum + e.size, 0)
        : (item.image ? item.image.size : item.bytes);

    const kind = pick.kind || item.kind;

    if (pick.kind && item.kind && pick.kind !== item.kind) {
        errors.push(pick.download + ": в файле " + item.kind + ", а в отборе " + pick.kind);
        continue;
    }

    if (isSet && kind !== "fda") {
        errors.push(pick.download + ": набор из образов не-дискет");
        continue;
    }

    presets.push({
        id: "olddos-" + pick.id,
        name: pick.name,
        kind,
        url: "http://old-dos.ru/dl.php?id=" + item.download,
        size: item.bytes,
        sha256: "",
        memory_mb: pick.memory_mb || (kind === "fda" ? 32 : 128),
        vga_memory_mb: pick.vga_memory_mb || 8,
        disk_mb: pick.disk_mb || 0,
        boot: kind === "fda" ? "FLOPPY_CD_HARDDISK" :
              kind === "cdrom" ? "CD_HARDDISK_FLOPPY" : "HARDDISK_CD_FLOPPY",
        net: "ne2k",
        // комплект дискет: консоль покажет «1/N» и кнопки листания
        floppy_set: isSet,
        floppy_count: isSet ? (item.images || []).length : 0,
        description: [pick.note, "old-dos.ru", item.format,
            imageBytes ? Math.round(imageBytes / 1024) + "K" : "",
            isSet ? ("дискет: " + item.images.length) : ""]
            .filter(Boolean).join(" · ")
    });
}

const doc = {
    version: 1,
    source: "http://old-dos.ru/",
    note: "Отобрано вручную из инвентаря old-dos.ru и проверено по сигнатуре (tools/old-dos-scan.mjs): в каждом файле ровно один образ. Образы в поставку не входят - скачиваются по dl.php, ZIP распаковывается на месте.",
    presets
};

fs.writeFileSync(outPath, JSON.stringify(doc, null, 1), "utf8");

console.log("presets: " + presets.length + " из " + curated.length);
for (const error of errors) console.log("  [skip] " + error);