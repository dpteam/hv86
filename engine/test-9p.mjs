/*
 * hv86 - самотест 9p-сервера (engine/fs9p.mjs).
 *
 * Гоняет протокол 9P2000.L напрямую, без гостя: version, attach, walk,
 * open/read/write, getattr, readdir, mkdir, rename, unlink и попытка
 * выйти за пределы шары. Так проверяется именно сервер, а не «повезло
 * с ядром Linux».
 *
 *   node engine/test-9p.mjs
 */
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { createShareHandler } from "./fs9p.mjs";

let failures = 0;

function ok(text) {
    console.log("  [ok]   " + text);
}

function fail(text) {
    failures++;
    console.log("  [FAIL] " + text);
}

function check(condition, text) {
    if (condition) ok(text);
    else fail(text);
}

// ------------------------------------------------------------- примитивы

class Reader {
    constructor(buffer, offset = 0) {
        this.buffer = buffer;
        this.view = new DataView(buffer.buffer, buffer.byteOffset, buffer.byteLength);
        this.offset = offset;
    }

    u8() { return this.view.getUint8(this.offset++); }
    u16() { const v = this.view.getUint16(this.offset, true); this.offset += 2; return v; }
    u32() { const v = this.view.getUint32(this.offset, true); this.offset += 4; return v; }
    u64() { const v = this.view.getBigUint64(this.offset, true); this.offset += 8; return Number(v); }
    str() { const n = this.u16(); const s = Buffer.from(this.buffer.subarray(this.offset, this.offset + n)).toString("utf8"); this.offset += n; return s; }
    bytes(n) { const b = Buffer.from(this.buffer.subarray(this.offset, this.offset + n)); this.offset += n; return b; }
}

function writer() {
    const chunks = [];
    return {
        u8(v) { chunks.push(Buffer.from([v & 0xff])); return this; },
        u16(v) { const b = Buffer.alloc(2); b.writeUInt16LE(v & 0xffff); chunks.push(b); return this; },
        u32(v) { const b = Buffer.alloc(4); b.writeUInt32LE(v >>> 0); chunks.push(b); return this; },
        u64(v) { const b = Buffer.alloc(8); b.writeBigUInt64LE(BigInt(Math.trunc(v))); chunks.push(b); return this; },
        str(v) { const b = Buffer.from(v ?? "", "utf8"); this.u16(b.length); chunks.push(b); return this; },
        bytes(b) { chunks.push(Buffer.from(b)); return this; },
        build() { return Buffer.concat(chunks); }
    };
}

function message(type, tag, body) {
    const payload = body ? body.build ? body.build() : body : Buffer.alloc(0);
    const head = Buffer.alloc(7);
    head.writeUInt32LE(payload.length + 7, 0);
    head.writeUInt8(type, 4);
    head.writeUInt16LE(tag, 5);
    return Buffer.concat([head, payload]);
}

function call(handler, request) {
    let response = null;
    handler(request, (buf) => { response = Buffer.from(buf); });
    if (!response) throw new Error("no reply");
    return new Reader(response);
}

function typeOf(reader) { return reader.buffer.readUInt8(4); }
function tagOf(reader) { return reader.buffer.readUInt16LE(5); }

// ------------------------------------------------------------------- тест

const root = fs.mkdtempSync(path.join(os.tmpdir(), "hv86-9p-"));
const outside = fs.mkdtempSync(path.join(os.tmpdir(), "hv86-9p-outside-"));

let outsideLinkCreated = false;

try {
    fs.writeFileSync(path.join(root, "hello.txt"), "hello 9p", "utf8");
    fs.mkdirSync(path.join(root, "sub"));
    fs.writeFileSync(path.join(root, "sub", "inner.txt"), "inner", "utf8");
    fs.writeFileSync(path.join(outside, "secret.txt"), "secret", "utf8");

    try {
        fs.symlinkSync(outside, path.join(root, "escape"), "junction");
        outsideLinkCreated = true;
    } catch {
        // без прав на симлинки тест просто пропускает эту часть
    }

    const handle = createShareHandler(root, { log: () => {} });

    // ---- version
    let reply = call(handle, message(100, 0xFFFF, writer().u32(8192).str("9P2000.L")));
    check(typeOf(reply) === 101, "Rversion");
    check(tagOf(reply) === 0xFFFF, "Rversion tag");

    let reader = new Reader(reply.buffer, 7);
    check(reader.u32() === 8192, "msize");
    check(reader.str() === "9P2000.L", "version name");

    // ---- attach
    reply = call(handle, message(104, 1, writer().u32(1).u32(0xFFFFFFFF).str("hv86").str("").u32(0)));
    check(typeOf(reply) === 105, "Rattach");
    check((reply.buffer[7] & 0x80) === 0x80, "root qid is a directory");

    // ---- walk + open + read
    reply = call(handle, message(110, 2, writer().u32(1).u32(2).u16(1).str("hello.txt")));
    check(typeOf(reply) === 111, "Rwalk hello.txt");
    check(new Reader(reply.buffer, 7).u16() === 1, "one qid walked");

    reply = call(handle, message(12, 3, writer().u32(2).u32(0)));
    check(typeOf(reply) === 13, "Rlopen");

    reply = call(handle, message(116, 4, writer().u32(2).u64(0).u32(64)));
    check(typeOf(reply) === 117, "Rread");

    reader = new Reader(reply.buffer, 7);
    const count = reader.u32();
    check(reader.bytes(count).toString("utf8") === "hello 9p", "read content");

    // ---- write
    reply = call(handle, message(118, 5, writer().u32(2).u64(0).u32(5).bytes(Buffer.from("HV86!"))));
    check(typeOf(reply) === 119, "Rwrite");
    check(fs.readFileSync(path.join(root, "hello.txt"), "utf8") === "HV86! 9p", "write landed on disk");

    // ---- getattr
    reply = call(handle, message(24, 6, writer().u32(2).u64(0x3FFF)));
    check(typeOf(reply) === 25, "Rgetattr");

    reader = new Reader(reply.buffer, 7);
    reader.u64();                                   // valid
    const qidType = reader.u8();
    reader.u32(); reader.u64();                     // qid version/path
    const mode = reader.u32();

    check((mode & 0x8000) === 0x8000, "getattr: regular file");
    check(qidType === 0, "getattr: file qid");

    // ---- clunk
    check(typeOf(call(handle, message(120, 7, writer().u32(2)))) === 121, "Rclunk");

    // ---- readdir
    reply = call(handle, message(110, 8, writer().u32(1).u32(3).u16(0)));
    check(typeOf(reply) === 111, "Rwalk (clone root)");

    check(typeOf(call(handle, message(12, 9, writer().u32(3).u32(0x10000)))) === 13, "Rlopen dir");

    reply = call(handle, message(40, 10, writer().u32(3).u64(0).u32(4096)));
    check(typeOf(reply) === 41, "Rreaddir");

    reader = new Reader(reply.buffer, 7);
    const dirBytes = reader.bytes(reader.u32());

    const names = [];
    let position = 0;

    while (position < dirBytes.length) {
        position += 13;                             // qid
        position += 8;                              // offset
        position += 1;                              // type
        const nameLength = dirBytes.readUInt16LE(position);
        position += 2;
        names.push(dirBytes.subarray(position, position + nameLength).toString("utf8"));
        position += nameLength;
    }

    check(names.includes("hello.txt") && names.includes("sub"), "readdir lists entries");

    // ---- mkdir + rename + unlink
    reply = call(handle, message(72, 11, writer().u32(1).str("newdir").u32(0o755).u32(0)));
    check(typeOf(reply) === 73, "Rmkdir");
    check(fs.existsSync(path.join(root, "newdir")), "mkdir created directory");

    reply = call(handle, message(74, 12,
        writer().u32(1).str("newdir").u32(1).str("renamed")));
    check(typeOf(reply) === 75, "Rrenameat");
    check(fs.existsSync(path.join(root, "renamed")), "rename landed");

    reply = call(handle, message(76, 13, writer().u32(1).str("renamed").u32(0x200)));
    check(typeOf(reply) === 77, "Runlinkat");
    check(!fs.existsSync(path.join(root, "renamed")), "unlink removed directory");

    // ---- выход за пределы шары
    reply = call(handle, message(110, 14, writer().u32(1).u32(9).u16(1).str("..")));
    check(typeOf(reply) === 7, "walk .. is refused");

    reader = new Reader(reply.buffer, 7);
    check(reader.u32() === 13, "walk .. -> EACCES");

    if (outsideLinkCreated) {
        reply = call(handle, message(110, 15, writer().u32(1).u32(10).u16(2).str("escape").str("secret.txt")));
        check(typeOf(reply) === 7, "symlink escape is refused");
    }

    // ---- только чтение
    const readOnly = createShareHandler(root, { readonly: true, log: () => {} });

    call(readOnly, message(100, 0xFFFF, writer().u32(8192).str("9P2000.L")));
    call(readOnly, message(104, 1, writer().u32(1).u32(0xFFFFFFFF).str("hv86").str("").u32(0)));
    call(readOnly, message(110, 2, writer().u32(1).u32(2).u16(1).str("hello.txt")));
    call(readOnly, message(12, 3, writer().u32(2).u32(2)));

    reply = call(readOnly, message(118, 4, writer().u32(2).u64(0).u32(2).bytes(Buffer.from("xx"))));
    check(typeOf(reply) === 7 && new Reader(reply.buffer, 7).u32() === 30, "read-only share returns EROFS");
} catch (e) {
    fail("exception: " + (e && e.stack || e));
} finally {
    try { fs.rmSync(root, { recursive: true, force: true }); } catch { }
    try { fs.rmSync(outside, { recursive: true, force: true }); } catch { }
}

console.log(failures === 0 ? "9P: OK" : "9P: FAILED (" + failures + ")");
process.exit(failures === 0 ? 0 : 1);