// Краулер old-dos.ru: категории -> списки -> страницы файлов.
//
// Зачем: собрать инвентарь образов (что вообще есть, какого типа, сколько весит),
// чтобы потом отобрать то, что реально загрузится в v86. Сайт отдаёт
// windows-1251, пагинация у списков - параметр `id`, у файлов - `dl.php?id=`.
//
// Запуск:
//   node tools/old-dos-crawl.mjs list  <out.json>       все категории и записи
//   node tools/old-dos-crawl.mjs files <out.json> [cat,cat,...]   страницы файлов
//
// Кэш ответов лежит в .old-dos-cache (в .gitignore): повторный запуск не
// дёргает сайт заново.
import fs from "node:fs";
import path from "node:path";

const BASE = "http://old-dos.ru";
const CACHE = path.join(process.cwd(), ".old-dos-cache");
const DELAY_MS = 120;

const [, , stage, output, catsArg] = process.argv;

if (!stage || !output) {
    console.error("usage: node tools/old-dos-crawl.mjs list|files <out.json> [categories]");
    process.exit(2);
}

fs.mkdirSync(CACHE, { recursive: true });

function cachePath(url) {
    const name = url
        .replace(BASE, "")
        .replace(/[^a-z0-9]+/gi, "_")
        .slice(0, 180);

    return path.join(CACHE, name + ".html");
}

function sleep(ms) {
    return new Promise((resolve) => setTimeout(resolve, ms));
}

async function get(url, useCache = true) {
    const file = cachePath(url);

    if (useCache && fs.existsSync(file)) {
        return new TextDecoder("windows-1251").decode(fs.readFileSync(file));
    }

    for (let attempt = 1; attempt <= 3; attempt++) {
        try {
            const response = await fetch(url, {
                headers: {
                    "User-Agent": "Mozilla/5.0 (hv86 catalog crawler; +https://github.com/dpteam/hv86)",
                    "Accept": "text/html"
                }
            });

            if (!response.ok) throw new Error("HTTP " + response.status);

            const buffer = new Uint8Array(await response.arrayBuffer());
            fs.writeFileSync(file, buffer);

            await sleep(DELAY_MS);

            return new TextDecoder("windows-1251").decode(buffer);
        } catch (e) {
            if (attempt === 3) throw new Error(url + ": " + e.message);
            await sleep(1200 * attempt);
        }
    }
}

function text(html) {
    return html
        .replace(/<script[\s\S]*?<\/script>/gi, " ")
        .replace(/<[^>]+>/g, " ")
        .replace(/&nbsp;/g, " ")
        .replace(/&amp;/g, "&")
        .replace(/&quot;/g, '"')
        .replace(/&laquo;/g, "«")
        .replace(/&raquo;/g, "»")
        .replace(/&minus;/g, "-")
        .replace(/&copy;/g, "©")
        .replace(/\s+/g, " ")
        .trim();
}

// ---------------------------------------------------------------- категории

async function crawlList() {
    const categories = new Map();     // cat -> { id, name, parent, pages }
    const entries = new Map();        // id -> { id, name, description, cat }
    const queue = [];

    const root = await get(BASE + "/index.php?page=files&mode=files");

    for (const m of root.matchAll(/href="[^"]*do=list&(?:amp;)?cat=(\d+)(?:&(?:amp;)?id=\d+)?"[^>]*>([\s\S]*?)<\/a>/gi)) {
        const id = Number(m[1]);
        const name = text(m[2]);

        if (!id || !name) continue;
        if (!categories.has(id)) categories.set(id, { id, name, pages: new Set() });
    }

    for (const id of categories.keys()) queue.push({ cat: id, page: 1 });

    const seenPages = new Set();
    let pages = 0;

    while (queue.length > 0) {
        const job = queue.shift();
        const key = job.cat + ":" + job.page;

        if (seenPages.has(key)) continue;
        seenPages.add(key);

        const url = BASE + "/index.php?page=files&mode=files&do=list&cat=" + job.cat + "&id=" + job.page;
        let html;

        try { html = await get(url); }
        catch (e) { console.error("[skip] " + e.message); continue; }

        pages++;
        if (pages % 20 === 0) console.log("  pages: " + pages + ", entries: " + entries.size);

        // записи на странице: ссылка show + блок описания
        for (const m of html.matchAll(/<a href="[^"]*do=show&(?:amp;)?id=(\d+)">([\s\S]*?)<\/a>/gi)) {
            const id = Number(m[1]);
            const name = text(m[2]);

            if (!id || !name || name.length < 2) continue;

            // описание идёт сразу после заголовка, в первой ячейке таблицы
            const tail = html.slice(m.index, m.index + 2000);
            const cells = [...tail.matchAll(/padding: 3px;">([\s\S]*?)<\/td>/gi)].map((c) => text(c[1]));

            if (!entries.has(id)) {
                entries.set(id, {
                    id,
                    name,
                    cat: job.cat,
                    description: cells[0] || "",
                    info: cells[1] || ""
                });
            }
        }

        // пагинация: ссылки с id=N на этой же категории
        const ids = new Set();

        for (const m of html.matchAll(/do=list&(?:amp;)?cat=(\d+)&(?:amp;)?id=(\d+)/gi)) {
            if (Number(m[1]) !== job.cat) continue;
            ids.add(Number(m[2]));
        }

        const pageNumbers = [...ids].filter((n) => n > 0);
        const maxPage = pageNumbers.length ? Math.max(...pageNumbers) : 1;

        for (let page = 2; page <= maxPage; page++) {
            const nextKey = job.cat + ":" + page;
            if (seenPages.has(nextKey)) continue;
            queue.push({ cat: job.cat, page });
        }

        categories.get(job.cat) && categories.get(job.cat).pages.add(job.page);
    }

    const result = {
        crawled_utc: new Date().toISOString(),
        pages,
        categories: [...categories.values()].map((c) => ({ id: c.id, name: c.name, pages: c.pages.size })),
        entries: [...entries.values()]
    };

    fs.writeFileSync(output, JSON.stringify(result, null, 1), "utf8");
    console.log("categories: " + result.categories.length + ", pages: " + pages + ", entries: " + entries.length);
}

// -------------------------------------------------------------- страницы файлов

function parseFiles(html, entryId) {
    const files = [];

    // каждая строка файла: <tr id="fileNNN"> ... dl.php?id=NNN ...
    const rows = html.split(/<tr id="file/).slice(1);

    for (const row of rows) {
        const body = "<tr id=\"file" + row;
        const link = body.match(/dl\.php\?id=(\d+)/);
        if (!link) continue;

        const downloaded = Number(link[1]);

        // тип файла закодирован иконкой с описанием в acronym
        const typeMatch = body.match(/<acronym title="([^"]+)">/i);
        const iconMatch = body.match(/img\/type(\d+)\.png/i);

        // имя файла: первая ячейка с текстом после иконки-ссылки
        const nameMatch = body.match(/<\/acronym><\/td><td>([\s\S]*?)<\/td>/i);

        const cells = [...body.matchAll(/padding: 3px;"[^>]*>([\s\S]*?)<\/td>/gi)].map((c) => text(c[1]));

        const sizeMatch = body.match(/align="right">([\d.,]+\s*[КМГ]?[бБ])<\/td>/i);
        const quality = /nofake\.png/i.test(body) ? "original" : (/fake\.png/i.test(body) ? "fake" : "");

        files.push({
            entry: entryId,
            download: downloaded,
            url: BASE + "/dl.php?id=" + downloaded,
            type: typeMatch ? text(typeMatch[1]) : "",
            icon: iconMatch ? "type" + iconMatch[1] : "",
            name: nameMatch ? text(nameMatch[1]) : "",
            size: sizeMatch ? sizeMatch[1].replace(/\s+/g, " ") : "",
            cells: cells.slice(0, 6),
            quality
        });
    }

    return files;
}

async function crawlFiles(onlyCategories) {
    const list = JSON.parse(fs.readFileSync(output.replace(/\.json$/, "") + ".list.json", "utf8"));
    const wanted = onlyCategories
        ? new Set(onlyCategories.split(",").map((s) => Number(s.trim())))
        : null;

    const entries = list.entries.filter((e) => !wanted || wanted.has(Number(e.cat)));

    console.log("entries to inspect: " + entries.length);

    const files = [];
    let done = 0;

    for (const entry of entries) {
        const url = BASE + "/index.php?page=files&mode=files&do=show&id=" + entry.id;
        let html;

        try { html = await get(url); }
        catch (e) { console.error("[skip] " + e.message); continue; }

        const parsed = parseFiles(html, entry.id);

        // в карточке есть раздел и требования
        const section = html.match(/Раздел:\s*<a[^>]*>([\s\S]*?)<\/a>/i);

        for (const file of parsed) {
            file.entryName = entry.name;
            file.cat = entry.cat;
            file.section = section ? text(section[1]) : "";
            files.push(file);
        }

        done++;
        if (done % 50 === 0) console.log("  cards: " + done + ", files: " + files.length);
    }

    fs.writeFileSync(output, JSON.stringify({
        crawled_utc: new Date().toISOString(),
        cards: done,
        files
    }, null, 1), "utf8");

    const types = new Map();
    for (const f of files) types.set(f.type, (types.get(f.type) || 0) + 1);

    console.log("files: " + files.length);
    for (const [type, count] of [...types].sort((a, b) => b[1] - a[1]))
        console.log("  " + String(count).padStart(6) + "  " + (type || "(no type)").slice(0, 90));
}

if (stage === "list") await crawlList();
else if (stage === "files") await crawlFiles(catsArg);
else {
    console.error("unknown stage: " + stage);
    process.exit(2);
}