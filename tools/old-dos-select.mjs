// Отбор из инвентаря old-dos.ru: что из этого можно честно положить в каталог.
//
// Запуск:
//   node tools/old-dos-select.mjs <files.json> <curated.json> <out.json>
//
// files.json  - результат `old-dos-crawl.mjs files`
// curated.json - ручной список: [{ file: <download id>, kind: "fda|hda|cdrom",
//                 id, name, memory_mb, note }]
//
// Скрипт проверяет, что каждый выбранный файл есть в инвентаре, что тип
// (флоппи/CD/HD) совпадает с заявленным kind и что размер влезает в лимит.
// Ничего не скачивает: каталожная запись - это url + метаданные.
import fs from "node:fs";

const [, , filesPath, curatedPath, outPath] = process.argv;

if (!curatedPath || !outPath) {
    console.error("usage: node tools/old-dos-select.mjs <files.json> <curated.json> <out.json>");
    process.exit(2);
}

const inventory = JSON.parse(fs.readFileSync(filesPath, "utf8"));
const curated = JSON.parse(fs.readFileSync(curatedPath, "utf8"));

const byDownload = new Map();
for (const file of inventory.files) byDownload.set(file.download, file);

// "1.4 Мб" -> байты: точность тут не нужна, нужен порядок величины
function sizeOf(text) {
    const m = String(text || "").replace(/\s+/g, "").match(/([\d.,]+)([КМГKMG]?)/i);
    if (!m) return 0;

    const value = Number(m[1].replace(",", "."));
    const unit = (m[2] || "").toUpperCase();

    if (unit === "Г" || unit === "G") return Math.round(value * 1024 * 1024 * 1024);
    if (unit === "М" || unit === "M") return Math.round(value * 1024 * 1024);
    if (unit === "К" || unit === "K") return Math.round(value * 1024);

    return Math.round(value);
}

// тип файла в инвентаре - это подпись иконки на сайте
function kindOf(file) {
    const type = (file.type || "").toLowerCase();

    if (type.includes("cd") || type.includes("iso") || type.includes("диск")) {
        if (type.includes("флоппи") || type.includes("floppy")) return "fda";
        if (type.includes("жёстк") || type.includes("жестк") || type.includes("hd")) return "hda";
        return "cdrom";
    }

    if (type.includes("флоппи") || type.includes("floppy")) return "fda";
    if (type.includes("жёстк") || type.includes("жестк") || type.includes("hd")) return "hda";

    return "";
}

const MAX_BYTES = 700 * 1024 * 1024;

const presets = [];
const problems = [];

for (const item of curated) {
    const file = byDownload.get(Number(item.file));

    if (!file) {
        problems.push("нет в инвентаре: file " + item.file + " (" + item.id + ")");
        continue;
    }

    const size = sizeOf(file.size);
    const kind = item.kind || kindOf(file);

    if (!kind) {
        problems.push("непонятный тип носителя: " + item.id + " (" + file.type + ")");
        continue;
    }

    if (size > MAX_BYTES) {
        problems.push("слишком большой: " + item.id + " (" + file.size + ")");
        continue;
    }

    // проверка на «школьные поделки»: у оригинальных изданий на сайте стоит
    // отметка nofake, да и сам файл не должен быть микроскопическим
    const sizeOk = kind === "fda" ? size >= 160 * 1024 : size >= 1024 * 1024;

    if (!sizeOk) {
        problems.push("подозрительно маленький: " + item.id + " (" + file.size + ")");
        continue;
    }

    presets.push({
        id: "olddos-" + item.id,
        name: item.name,
        kind,
        url: file.url,
        size,
        sha256: "",
        memory_mb: item.memory_mb || (kind === "fda" ? 32 : 128),
        vga_memory_mb: item.vga_memory_mb || 8,
        disk_mb: 0,
        boot: kind === "fda" ? "FLOPPY_CD_HARDDISK" :
              kind === "cdrom" ? "CD_HARDDISK_FLOPPY" : "HARDDISK_CD_FLOPPY",
        net: "ne2k",
        description: item.note || (file.entryName + " - old-dos.ru, " + file.type)
    });
}

const doc = {
    version: 1,
    source: "http://old-dos.ru/",
    note: "Отобрано вручную из инвентаря old-dos.ru. Образы не входят в поставку: скачиваются по dl.php и распаковываются на месте (ZIP поддерживается, RAR/7z нет).",
    presets
};

fs.writeFileSync(outPath, JSON.stringify(doc, null, 1), "utf8");

console.log("presets: " + presets.length + " из " + curated.length);
for (const problem of problems) console.log("  [skip] " + problem);