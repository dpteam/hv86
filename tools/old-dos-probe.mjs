// Разведка структуры old-dos.ru: печатает ссылки с найденной страницы.
// Запуск: node tools/old-dos-probe.mjs <url> [фильтр]
const [, , url, filter] = process.argv;

const response = await fetch(url, {
    headers: { "User-Agent": "Mozilla/5.0 (hv86 catalog probe)" }
});

const buffer = new Uint8Array(await response.arrayBuffer());
const text = new TextDecoder("windows-1251").decode(buffer);

console.log("status: " + response.status + ", bytes: " + buffer.length);

const links = new Map();

for (const match of text.matchAll(/<a\s+[^>]*href="([^"]+)"[^>]*>([\s\S]*?)<\/a>/gi)) {
    const href = match[1];
    const label = match[2].replace(/<[^>]+>/g, "").replace(/&nbsp;/g, " ").trim();

    if (filter && !href.includes(filter) && !label.includes(filter)) continue;
    if (label.length === 0) continue;

    if (!links.has(href)) links.set(href, label);
}

console.log("links: " + links.size);

for (const [href, label] of links) {
    console.log("  " + href.replace(/^https?:\/\/[^/]+/, "") + "  |  " + label.slice(0, 70));
}