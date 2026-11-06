/*
 * hv86 - менеджер (страница в WebView2).
 *
 * Логики про ВМ здесь нет: страница рисует то, что прислал хост, и отправляет
 * команды. Всё остальное - в C# (служба + vmhost).
 *
 * Хост -> страница:
 *   {type:"state", service:{...}, host:{...}, vms:[...], presets:[...], ...}
 *   {type:"event", name, data}
 *   {type:"snapshots", id, items}
 *   {type:"toast", text, kind}
 *   {type:"error", message}
 *   {type:"log", text}
 *   {type:"picked", path}
 *
 * Страница -> хост:
 *   {action:"ready"|"refresh"|"vm.start"|..., id?, ...}
 */
(function () {
    "use strict";

    var post = function (message) {
        try { chrome.webview.postMessage(message); } catch (e) { /* окно закрывается */ }
    };

    // без хоста (предпросмотр в обычном браузере) страница работает на
    // демо-данных: так вёрстку можно смотреть, не собирая exe
    var hasHost = !!(window.chrome && window.chrome.webview && window.chrome.webview.postMessage);

    var byId = function (id) { return document.getElementById(id); };

    var I18N = window.I18N || {};
    var lang = localStorage.getItem("hv86.lang") || (navigator.language || "ru").slice(0, 2);
    if (!I18N[lang]) lang = "ru";

    function T(key) {
        var dict = I18N[lang] || {};
        if (dict[key] !== undefined) return dict[key];
        var fallback = I18N.ru || {};
        if (fallback[key] !== undefined) return fallback[key];
        return key;
    }

    var state = {
        service: null,
        host: null,
        vms: [],
        presets: [],
        selected: null,
        snapshotsId: null,
        pendingPickTarget: null,
        fwdDraft: []
    };

    // ------------------------------------------------------------- утилиты

    function esc(value) {
        return String(value === null || value === undefined ? "" : value)
            .replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;")
            .replace(/"/g, "&quot;").replace(/'/g, "&#39;");
    }

    function fmtUptime(seconds) {
        var s = Number(seconds) || 0;
        if (s <= 0) return "—";

        var h = Math.floor(s / 3600);
        var m = Math.floor((s % 3600) / 60);

        if (h > 0) return h + "h " + (m < 10 ? "0" : "") + m + "m";
        if (m > 0) return m + "m " + ((s % 60) < 10 ? "0" : "") + (s % 60) + "s";
        return s + "s";
    }

    // Режим отказоустойчивости в одну строку: "replica 3s · warm"
    // (у реплики важно видеть, прогрета ли она - от этого зависит повышение)
    function ftLabel(vm) {
        var mode = vm.ft_mode || "off";
        if (mode === "off") return "off";

        var text = mode + " " + Math.round((vm.interval_ms || 0) / 1000) + "s";

        if (mode === "replica" && vm.replica) text += " · " + vm.replica;

        return text;
    }

    function fmtSize(bytes) {
        var b = Number(bytes) || 0;
        if (b <= 0) return "—";
        if (b >= 1024 * 1024 * 1024) return (b / 1024 / 1024 / 1024).toFixed(1) + " GB";
        if (b >= 1024 * 1024) return Math.round(b / 1024 / 1024) + " MB";
        return Math.round(b / 1024) + " KB";
    }

    function fmtDate(iso) {
        if (!iso) return "—";
        try {
            var d = new Date(iso);
            return d.toLocaleString();
        } catch (e) {
            return iso;
        }
    }

    function stateClass(value) {
        var s = String(value || "");
        if (s === "running" || s === "paused" || s === "failed" || s === "recovering" || s === "starting") return s;
        return "";
    }

    function vmById(id) {
        for (var i = 0; i < state.vms.length; i++) {
            if (state.vms[i].id === id) return state.vms[i];
        }
        return null;
    }

    function selectedVm() {
        return vmById(state.selected);
    }

    // ------------------------------------------------------------ уведомления

    function toast(text, kind) {
        if (!text) return;

        var box = document.createElement("div");
        box.className = "toast " + (kind || "");
        box.textContent = text;
        byId("toasts").appendChild(box);

        setTimeout(function () {
            box.style.opacity = "0";
            box.style.transition = "opacity .2s";
            setTimeout(function () { if (box.parentNode) box.parentNode.removeChild(box); }, 220);
        }, kind === "bad" ? 6000 : 3200);
    }

    function logLine(text, kind) {
        if (!text) return;

        var list = byId("logList");
        var line = document.createElement("div");
        line.className = "log-line " + (kind || "");

        var time = document.createElement("span");
        time.className = "t";
        time.textContent = new Date().toLocaleTimeString();
        line.appendChild(time);

        var body = document.createElement("span");
        body.textContent = text;
        line.appendChild(body);

        list.appendChild(line);

        while (list.childNodes.length > 300) list.removeChild(list.firstChild);
        list.scrollTop = list.scrollHeight;
    }

    // -------------------------------------------------------------- отрисовка

    function render() {
        renderService();
        renderTree();
        renderTable();
        renderActions();
        renderStatus();
    }

    function renderService() {
        var service = state.service || { installed: false, running: false, state: "unknown" };

        var pill = byId("servicePill");
        var text = byId("serviceText");

        pill.className = "pill " + (service.running ? "ok" : service.installed ? "warn" : "bad");

        if (!service.installed) text.textContent = T("svc.notInstalled");
        else if (service.running) text.textContent = T("svc.running");
        else text.textContent = T("svc.stopped");
    }

    function renderTree() {
        var host = byId("treeVms");
        host.innerHTML = "";

        for (var i = 0; i < state.vms.length; i++) {
            var vm = state.vms[i];

            var row = document.createElement("div");
            row.className = "tree-vm" + (vm.id === state.selected ? " active" : "");
            row.setAttribute("data-id", vm.id);

            var dot = document.createElement("span");
            dot.className = "dot " + stateClass(vm.state);

            var name = document.createElement("span");
            name.className = "nm";
            name.textContent = vm.name;

            row.appendChild(dot);
            row.appendChild(name);

            host.appendChild(row);
        }

        byId("hostBit").textContent = state.vms.length + " vm";
    }

    function renderTable() {
        var body = byId("vmRows");
        body.innerHTML = "";

        byId("emptyState").classList.toggle("hidden", state.vms.length > 0);
        byId("vmCount").textContent = state.vms.length + " / " +
            (state.host && state.host.vms_running !== undefined ? state.host.vms_running : 0) + " running";

        for (var i = 0; i < state.vms.length; i++) {
            var vm = state.vms[i];

            var tr = document.createElement("tr");
            if (vm.id === state.selected) tr.className = "active";
            tr.setAttribute("data-id", vm.id);

            var media = [];
            if (vm.disk) media.push(vm.disk);
            if (vm.cdrom) media.push(vm.cdrom);
            if (vm.floppy) media.push(vm.floppy);

            tr.innerHTML =
                '<td class="name">' + esc(vm.name) + '</td>' +
                '<td><span class="state ' + stateClass(vm.state) + '">' + esc(T("state." + vm.state, vm.state)) + '</span></td>' +
                '<td class="mono">' + esc(vm.memory_mb) + ' MB</td>' +
                '<td class="mono">' + esc(media.join(" · ") || "—") + '</td>' +
                '<td class="mono">' + (vm.mips ? esc(vm.mips) : "—") + '</td>' +
                '<td class="mono">' + (vm.state === "running" || vm.state === "paused" ? fmtUptime(vm.uptime_sec) : "—") + '</td>' +
                '<td class="mono">' + esc(ftLabel(vm)) + '</td>' +
                '<td class="mono">' + esc(vm.restarts || 0) + '</td>';

            body.appendChild(tr);
        }
    }

    function renderActions() {
        var vm = selectedVm();

        byId("selectedName").textContent = vm ? vm.name : "—";

        var buttons = document.querySelectorAll("[data-vm='1']");

        for (var i = 0; i < buttons.length; i++) {
            var button = buttons[i];
            var act = button.getAttribute("data-act");
            var enabled = !!vm;

            if (vm) {
                var running = vm.state === "running";
                var active = running || vm.state === "paused" || vm.state === "starting" || vm.state === "recovering";
                var idle = vm.state === "stopped" || vm.state === "failed";

                if (act === "vm.start") enabled = !active;
                if (act === "vm.pause") enabled = running;
                if (act === "vm.reset") enabled = running || vm.state === "paused";
                if (act === "vm.stop") enabled = active;
                if (act === "vm.snapshot") enabled = running;
                if (act === "vm.eject") enabled = !!vm.cdrom;
                if (act === "vm.clone" || act === "vm.export") enabled = idle;
            }

            button.disabled = !enabled;
        }
    }

    function renderStatus() {
        var service = state.service || {};
        var host = state.host || {};

        byId("statusService").textContent = "service: " + (service.state || "unknown");

        byId("statusHost").textContent = host.product
            ? "host: " + host.product + " " + host.version + " · v86 " + host.v86 +
              " · session " + host.session + " · " + host.vms_total + " vm"
            : "host: not connected";

        byId("statusData").textContent = state.data_dir || (host.data_dir || "");
        byId("statusVersion").textContent = "hv86 " + (state.version || "") + " · v86 " + (state.v86 || "");
    }

    // --------------------------------------------------------------- действия

    function select(id) {
        state.selected = id;
        renderTree();
        renderTable();
        renderActions();
    }

    function openModal(id) {
        hideCtx();
        byId(id).classList.remove("hidden");
    }

    function closeModal(id) {
        byId(id).classList.add("hidden");
    }

    function fillPresets() {
        var select = byId("newPreset");
        select.innerHTML = '<option value="">' + esc(T("new.noPreset")) + '</option>';

        for (var i = 0; i < state.presets.length; i++) {
            var preset = state.presets[i];
            var option = document.createElement("option");
            option.value = preset.id;
            option.textContent = preset.name + (preset.size ? " · " + fmtSize(preset.size) : "");
            select.appendChild(option);
        }
    }

    function applyPreset() {
        var id = byId("newPreset").value;
        var preset = null;

        for (var i = 0; i < state.presets.length; i++) {
            if (state.presets[i].id === id) preset = state.presets[i];
        }

        byId("newPresetHint").textContent = preset ? (preset.description || "") : T("new.noPresetHint");

        if (!preset) return;

        byId("newMemory").value = String(preset.memory_mb || 128);
        byId("newDisk").value = String(preset.disk_mb || 0);

        if (!byId("newName").value) byId("newName").value = preset.name.toLowerCase().replace(/[^a-z0-9]+/g, "-");
    }

    function openNewVm() {
        fillPresets();
        applyPreset();

        if (!byId("newName").value) byId("newName").value = "vm-" + Math.random().toString(16).slice(2, 6);

        openModal("newVmModal");
    }

    function createVm() {
        var preset = byId("newPreset").value;

        if (!preset && !byId("newIso").value.trim()) {
            toast(T("new.needSource"), "bad");
            return;
        }

        post({
            action: "vm.create",
            name: byId("newName").value.trim(),
            preset: preset,
            memory_mb: Number(byId("newMemory").value) || 128,
            disk_mb: Number(byId("newDisk").value) || 0,
            iso_path: byId("newIso").value.trim(),
            autostart: byId("newAutostart").checked
        });

        closeModal("newVmModal");
        toast(T("new.creating"), "info");
        logLine(T("new.creating"));
    }

    // ------------------------------------------------------------ настройки ВМ

    function openSettings() {
        var vm = selectedVm();
        if (!vm) return;

        byId("setName").value = vm.name || "";
        byId("setMemory").value = String(vm.memory_mb || 128);
        byId("setAutostart").checked = !!vm.autostart;
        byId("setEngine").value = vm.engine === "headless" ? "headless" : "web";
        byId("setNetMode").value = vm.net_mode === "relay" ? "relay" : "nat";
        byId("setSharePath").value = vm.share_path || "";
        byId("setShareReadOnly").checked = vm.share_readonly !== false;
        byId("setEgressMode").value = vm.egress_mode === "safe" ? "safe" : (vm.egress_mode === "allowlist" ? "allowlist" : "any");
        byId("setEgressAllow").value = (vm.egress_allow || []).join("\n");

        byId("setFtMode").value = vm.ft_mode === "checkpoint" ? "checkpoint" : (vm.ft_mode === "replica" ? "replica" : "off");
        byId("setInterval").value = Math.max(2, Math.round((vm.interval_ms || 15000) / 1000));
        byId("setKeep").value = vm.ft_keep || 3;
        byId("setRestart").value = vm.restart || "on-failure";
        byId("setAttempts").value = vm.max_attempts === undefined ? 5 : vm.max_attempts;
        byId("setRestore").checked = vm.restore_on_crash !== false;

        state.fwdDraft = (vm.forwards || []).slice();
        renderForwards();

        byId("fwdHost").value = "";
        byId("fwdGuest").value = "";

        openModal("settingsModal");
    }

    // ------------------------------------------------------- проброс портов

    function renderForwards() {
        var list = byId("fwdList");
        list.innerHTML = "";

        var relayMode = byId("setNetMode").value === "relay";
        var addButton = document.querySelector('[data-act="fwd.add"]');

        if (addButton) addButton.disabled = relayMode;

        if (relayMode) {
            var note = document.createElement("div");
            note.className = "hint";
            note.textContent = T("set.netRelayNote");
            list.appendChild(note);
            return;
        }

        if (!state.fwdDraft.length) {
            var empty = document.createElement("div");
            empty.className = "dim";
            empty.style.fontSize = "12px";
            empty.textContent = T("set.fwdEmpty");
            list.appendChild(empty);
            return;
        }

        state.fwdDraft.forEach(function (rule, index) {
            var row = document.createElement("div");
            row.className = "snap-row";
            row.innerHTML =
                '<div class="info"><div class="nm mono">' + esc(rule.bind || "127.0.0.1") + ":" + esc(rule.host_port) +
                ' → guest:' + esc(rule.guest_port) + '</div></div>' +
                '<div class="ops"><button class="btn danger" data-fwd-remove="' + index + '">✕</button></div>';
            list.appendChild(row);
        });
    }

    function addForward() {
        if (byId("setNetMode").value === "relay") {
            toast(T("set.netRelayNote"), "bad");
            return;
        }

        var host = Number(byId("fwdHost").value);
        var guest = Number(byId("fwdGuest").value);

        if (!host || !guest || host < 1 || host > 65535 || guest < 1 || guest > 65535) {
            toast(T("set.fwdBad"), "bad");
            return;
        }

        for (var i = 0; i < state.fwdDraft.length; i++)
            if (state.fwdDraft[i].host_port === host) {
                toast(T("set.fwdDup"), "bad");
                return;
            }

        state.fwdDraft.push({ host_port: host, guest_port: guest, bind: "127.0.0.1" });
        byId("fwdHost").value = "";
        byId("fwdGuest").value = "";

        renderForwards();
    }

    function saveSettings() {
        var vm = selectedVm();
        if (!vm) return;

        post({
            action: "vm.update",
            id: vm.id,
            name: byId("setName").value.trim(),
            memory_mb: Number(byId("setMemory").value) || vm.memory_mb,
            autostart: byId("setAutostart").checked,
            ft_mode: byId("setFtMode").value,
            ft_interval_ms: Math.max(2, Number(byId("setInterval").value) || 15) * 1000,
            ft_keep: Math.max(1, Number(byId("setKeep").value) || 3),
            restart: byId("setRestart").value,
            max_attempts: Math.max(0, Number(byId("setAttempts").value) || 0),
            restore_on_crash: byId("setRestore").checked,
            net_mode: byId("setNetMode").value,
            engine: byId("setEngine").value,
            share_path: byId("setSharePath").value,
            share_readonly: byId("setShareReadOnly").checked,
            egress_mode: byId("setEgressMode").value,
            egress_allow: byId("setEgressAllow").value.split("\n").map(function (line) { return line.trim(); }).filter(function (line) { return line.length > 0; }),
            forwards: state.fwdDraft
        });

        closeModal("settingsModal");
    }

    function ask(options, done) {
        byId("askTitle").textContent = options.title || "";
        byId("askLabel").textContent = options.label || "";
        byId("askInput").value = options.value || "";
        byId("askInput").classList.toggle("hidden", !!options.hideInput);

        var ok = byId("askOk");

        ok.onclick = function () {
            closeModal("askModal");
            done(byId("askInput").value);
        };

        openModal("askModal");
        byId("askInput").focus();
    }

    function renderSnapshots(id, items) {
        state.snapshotsId = id;

        var list = byId("snapList");
        list.innerHTML = "";

        byId("snapEmpty").classList.toggle("hidden", items && items.length > 0);
        openModal("snapModal");

        if (!items) return;

        items.forEach(function (snapshot) {
            var row = document.createElement("div");
            row.className = "snap-row";

            row.innerHTML =
                '<div class="info">' +
                    '<div class="nm">' + esc(snapshot.name || snapshot.id) + '</div>' +
                    '<div class="meta">' + esc(fmtDate(snapshot.created_utc)) +
                    ' · state ' + esc(fmtSize(snapshot.size)) +
                    ' · disk ' + (snapshot.disk_copy ? "included" : "no copy") +
                    ' · ' + esc((snapshot.instructions || 0).toLocaleString()) + ' instr</div>' +
                '</div>' +
                '<div class="ops">' +
                    '<button class="btn" data-snap-revert="' + esc(snapshot.id) + '">' + esc(T("snap.revert")) + '</button>' +
                    '<button class="btn danger" data-snap-delete="' + esc(snapshot.id) + '">' + esc(T("snap.delete")) + '</button>' +
                '</div>';

            list.appendChild(row);
        });
    }

    function handleAction(act, element) {
        var id = state.selected;

        switch (act) {
            case "refresh": post({ action: "refresh" }); return;
            case "new": openNewVm(); return;
            case "log.clear": byId("logList").innerHTML = ""; return;

            case "pick.file":
                state.pendingPickTarget = element.getAttribute("data-target") || "newIso";
                post({ action: "pick.file", kind: "image" });
                return;

            case "pick.folder":
                state.pendingPickTarget = element.getAttribute("data-target") || "setSharePath";
                post({ action: "pick.folder" });
                return;

            case "share.clear":
                byId("setSharePath").value = "";
                return;

            case "vm.create": createVm(); return;

            case "vm.settings": openSettings(); return;
            case "vm.update": saveSettings(); return;
            case "fwd.add": addForward(); return;

            case "vm.clone":
                ask({
                    title: T("clone.title"),
                    label: T("clone.label"),
                    value: ((vmById(id) || {}).name || "vm") + " (clone)"
                }, function (name) {
                    post({ action: "vm.clone", id: id, name: name });
                    toast(T("clone.doing"), "info");
                });
                return;

            case "vm.export":
                post({ action: "vm.export", id: id, name: (vmById(id) || {}).name || "" });
                return;

            case "vm.import":
                post({ action: "vm.import" });
                return;

            case "host.diagnostics":
                post({ action: "host.diagnostics" });
                toast(T("diag.collecting"), "info");
                return;

            case "vm.snapshot":
                ask({
                    title: T("snap.askTitle"),
                    label: T("snap.askLabel"),
                    value: "snap-" + new Date().toISOString().replace(/[:.]/g, "-").slice(0, 19)
                }, function (name) {
                    post({ action: "vm.snapshot", id: id, name: name });
                    toast(T("snap.taking"), "info");
                });
                return;

            case "vm.snapshots":
                post({ action: "vm.snapshots", id: id });
                return;

            case "vm.delete":
                ask({
                    title: T("del.title"),
                    label: T("del.label"),
                    hideInput: true
                }, function () {
                    post({ action: "vm.delete", id: id });
                    toast(T("del.done"), "info");
                });
                return;

            case "vm.mount":
                post({ action: "vm.mount", id: id, kind: "cdrom" });
                return;

            case "vm.eject":
                post({ action: "vm.eject", id: id, kind: "cdrom" });
                return;

            case "open.vm":
            case "open.logs":
            case "host.log":
            case "service.install":
            case "service.start":
            case "service.stop":
            case "service.uninstall":
                post({ action: act });
                return;

            default:
                if (act && act.indexOf("vm.") === 0) {
                    post({ action: act, id: id });
                    return;
                }
        }
    }

    // ------------------------------------------------------------------ события

    document.addEventListener("click", function (event) {
        var target = event.target;

        while (target && target !== document && !target.getAttribute) target = target.parentNode;

        var closeId = target.getAttribute && target.getAttribute("data-close");
        if (closeId) { closeModal(closeId); return; }

        var snapRevert = target.getAttribute && target.getAttribute("data-snap-revert");
        if (snapRevert) {
            ask({
                title: T("snap.revertTitle"),
                label: T("snap.revertLabel"),
                hideInput: true
            }, function () {
                post({ action: "vm.revert", id: state.snapshotsId, snapshot: snapRevert });
                toast(T("snap.reverting"), "info");
                closeModal("snapModal");
            });
            return;
        }

        var snapDelete = target.getAttribute && target.getAttribute("data-snap-delete");
        if (snapDelete) {
            post({ action: "vm.snapshot.delete", id: state.snapshotsId, snapshot: snapDelete });
            return;
        }

        var forwardRemove = target.getAttribute && target.getAttribute("data-fwd-remove");
        if (forwardRemove !== null && forwardRemove !== undefined && forwardRemove !== "") {
            state.fwdDraft.splice(Number(forwardRemove), 1);
            renderForwards();
            return;
        }

        var act = target.getAttribute && target.getAttribute("data-act");
        if (act) { handleAction(act, target); return; }

        // выбор ВМ в дереве/таблице
        var holder = target;
        while (holder && holder !== document) {
            var vmId = holder.getAttribute && holder.getAttribute("data-id");
            if (vmId) { select(vmId); return; }
            holder = holder.parentNode;
        }
    });

    byId("newPreset").addEventListener("change", applyPreset);
    byId("setNetMode").addEventListener("change", renderForwards);
    byId("lang").value = lang;

    byId("lang").addEventListener("change", function () {
        lang = this.value;
        localStorage.setItem("hv86.lang", lang);
        applyI18n();
        render();
    });

    function applyI18n() {
        var nodes = document.querySelectorAll("[data-i18n]");

        for (var i = 0; i < nodes.length; i++) {
            var key = nodes[i].getAttribute("data-i18n");
            var text = T(key);

            if (text !== key) nodes[i].textContent = text;
        }

        byId("fwdHost").placeholder = T("set.fwdHost");
        byId("fwdGuest").placeholder = T("set.fwdGuest");
    }

    // ------------------------------------------------------------- сообщения

    if (hasHost) window.chrome.webview.addEventListener("message", function (event) {
        var message = event.data;
        if (!message || typeof message !== "object") return;

        switch (message.type) {
            case "state":
                state.service = message.service || null;
                state.host = message.host || null;
                state.vms = message.vms || [];
                state.presets = message.presets || [];
                state.version = message.version || "";
                state.v86 = message.v86 || "";
                state.data_dir = message.data_dir || "";

                if (!vmById(state.selected) && state.vms.length > 0) state.selected = state.vms[0].id;
                if (state.vms.length === 0) state.selected = null;

                render();
                break;

            case "event":
                handleEvent(message.name, message.data || {});
                break;

            case "snapshots":
                renderSnapshots(message.id, message.items || []);
                if (message.error) toast(message.error, "bad");
                break;

            case "toast":
                toast(message.text, message.kind);
                logLine(message.text, message.kind === "bad" ? "bad" : "ok");
                break;

            case "error":
                toast(message.message, "bad");
                logLine(message.message, "bad");
                break;

            case "log":
                message.text.split("\n").forEach(function (line) { if (line.trim()) logLine(line); });
                break;

            case "picked":
                if (state.pendingPickTarget && message.path) byId(state.pendingPickTarget).value = message.path;
                state.pendingPickTarget = null;
                break;
        }
    });

    function handleEvent(name, data) {
        var vm = data.id ? vmById(data.id) : null;
        var label = vm ? vm.name : (data.id || "");

        switch (name) {
            case "vm.state":
                logLine(label + ": " + T("state." + data.state, data.state) +
                    (data.error ? " — " + data.error : ""),
                    data.state === "failed" ? "bad" : data.state === "recovering" ? "warn" : "");
                break;

            case "ft.event":
                logLine("[" + (data.kind || "ft") + "] " + label + ": " + (data.text || ""),
                    data.kind === "crash" || data.kind === "gave-up" ? "bad" :
                    data.kind === "restart" || data.kind === "restored" ? "warn" : "");
                break;

            case "vm.toast":
                toast(data.text, data.kind);
                logLine(label + ": " + data.text);
                break;

            case "vm.metrics":
                break;

            case "vm.log":
                logLine(label + ": " + (data.text || ""));
                break;

            case "vm.snapshot":
                toast(T("snap.done"), "ok");
                logLine(label + ": snapshot " + ((data.snapshot || {}).name || ""));
                break;

            case "vm.error":
                logLine(label + ": " + (data.message || ""), "bad");
                break;

            case "vm.forward":
                logLine(label + ": [net] " + (data.text || ""));
                break;

            default:
                logLine(name + " " + label);
        }
    }

    // ------------------------------------------------------- контекстное меню

    var ctx = byId("ctxMenu");

    function hideCtx() {
        ctx.classList.add("hidden");
    }

    function ctxItem(label, act, id, enabled, danger) {
        var button = document.createElement("button");
        button.textContent = label;
        button.disabled = !enabled;
        if (danger) button.className = "danger";

        button.addEventListener("click", function () {
            hideCtx();
            handleAction(act, button);
        });

        ctx.appendChild(button);
    }

    function ctxSeparator() {
        var sep = document.createElement("div");
        sep.className = "sep";
        ctx.appendChild(sep);
    }

    function showContextMenu(event, id) {
        select(id);

        var vm = vmById(id);
        if (!vm) return;

        var running = vm.state === "running";
        var paused = vm.state === "paused";
        var active = running || paused || vm.state === "starting" || vm.state === "recovering";
        var idle = vm.state === "stopped" || vm.state === "failed";

        ctx.innerHTML = "";

        ctxItem(T("act.connect"), "vm.console", id, true);
        ctxItem(running ? T("act.pause") : T("act.resume"), running ? "vm.pause" : "vm.resume", id, running || paused);
        ctxItem(T("act.reset"), "vm.reset", id, running || paused);
        ctxItem(T("act.stop"), "vm.stop", id, active);
        ctxSeparator();
        ctxItem(T("act.snapshot"), "vm.snapshot", id, running);
        ctxItem(T("act.snapshotsList"), "vm.snapshots", id, true);
        ctxSeparator();
        ctxItem(T("act.settings"), "vm.settings", id, true);
        ctxItem(T("act.clone"), "vm.clone", id, idle);
        ctxItem(T("act.export"), "vm.export", id, idle);
        ctxSeparator();
        ctxItem(T("act.openFolder"), "open.vm", id, true);
        ctxItem(T("act.delete"), "vm.delete", id, true, true);

        ctx.classList.remove("hidden");

        var box = ctx.getBoundingClientRect();
        ctx.style.left = Math.max(6, Math.min(event.clientX, window.innerWidth - box.width - 8)) + "px";
        ctx.style.top = Math.max(6, Math.min(event.clientY, window.innerHeight - box.height - 8)) + "px";
    }

    document.addEventListener("contextmenu", function (event) {
        var holder = event.target;

        while (holder && holder !== document) {
            var vmId = holder.getAttribute && holder.getAttribute("data-id");
            if (vmId) {
                event.preventDefault();
                showContextMenu(event, vmId);
                return;
            }
            holder = holder.parentNode;
        }

        hideCtx();
    });

    document.addEventListener("mousedown", function (event) {
        if (!ctx.contains(event.target)) hideCtx();
    });

    document.addEventListener("click", function (event) {
        if (!ctx.contains(event.target)) hideCtx();
    });

    window.addEventListener("blur", hideCtx);
    document.addEventListener("scroll", hideCtx, true);

    // --------------------------------------------------------- горячие клавиши

    function anyModalOpen() {
        var modals = ["newVmModal", "snapModal", "askModal", "settingsModal"];

        for (var i = 0; i < modals.length; i++)
            if (!byId(modals[i]).classList.contains("hidden")) return true;

        return false;
    }

    document.addEventListener("keydown", function (event) {
        if (anyModalOpen()) return;
        if (event.target && /^(INPUT|SELECT|TEXTAREA)$/.test(event.target.tagName)) return;

        var vm = selectedVm();
        if (!vm) return;

        switch (event.key) {
            case "F5":
                event.preventDefault();
                post({ action: "vm.start", id: vm.id });
                break;

            case "F6":
                event.preventDefault();
                post({ action: vm.state === "paused" ? "vm.resume" : "vm.pause", id: vm.id });
                break;

            case "F2":
                event.preventDefault();
                openSettings();
                break;

            case "Enter":
                event.preventDefault();
                post({ action: "vm.console", id: vm.id });
                break;

            case "Delete":
                event.preventDefault();
                handleAction("vm.delete", null);
                break;

            case "Escape":
                hideCtx();
                break;
        }
    });

    byId("askInput").addEventListener("keydown", function (event) {
        if (event.key !== "Enter") return;
        event.preventDefault();
        byId("askOk").click();
    });

    // -------------------------------------------------------------------- старт

    applyI18n();
    render();

    if (hasHost) post({ action: "ready" });
    else preview();

    // ------------------------------------------------------------- предпросмотр

    function preview() {
        state.service = { installed: true, running: true, state: "running" };
        state.host = {
            product: "hv86 Hypervisor", version: "0.1.0", v86: "0.5.473",
            session: 1, vms_total: 3, vms_running: 2
        };
        state.version = "0.1.0";
        state.v86 = "0.5.473";
        state.data_dir = "C:\\ProgramData\\hv86";

        state.vms = [
            {
                id: "a1b2c3d4", name: "tinycore-dev", state: "running", memory_mb: 128,
                disk: "system.img", cdrom: "boot.iso", mips: 42, uptime_sec: 5321,
                ft_mode: "checkpoint", interval_ms: 15000, restarts: 1, instructions: 214000000
            },
            {
                id: "e5f6a7b8", name: "freedos-lab", state: "paused", memory_mb: 32,
                floppy: "floppy.img", mips: 0, uptime_sec: 640,
                ft_mode: "checkpoint", interval_ms: 15000, restarts: 0
            },
            {
                id: "c9d0e1f2", name: "kolibri", state: "stopped", memory_mb: 64,
                floppy: "kolibri.img", mips: 0, uptime_sec: 0, ft_mode: "off", restarts: 2
            }
        ];

        state.presets = [
            { id: "freedos", name: "FreeDOS 7.22", size: 737280, memory_mb: 32, disk_mb: 0,
              description: "Bootable 720K FreeDOS floppy. Boots in about a second." },
            { id: "kolibrios", name: "KolibriOS", size: 1474560, memory_mb: 64, disk_mb: 0,
              description: "KolibriOS on a 1.44M floppy: graphical, fast." },
            { id: "tinycore", name: "TinyCore Linux 11", size: 19922944, memory_mb: 128, disk_mb: 256,
              description: "TinyCore Linux live CD with a 256M system disk attached." }
        ];

        state.selected = state.vms[0].id;

        render();

        logLine("hv86 0.1.0 · v86 0.5.473", "ok");
        logLine("service: running");
        logLine("tinycore-dev: running");
        logLine("[checkpoint] tinycore-dev: Чекпоинт за 212 мс", "");
        logLine("[crash] freedos-lab: процесс ВМ завершился (код 1)", "bad");
        logLine("[restart] freedos-lab: Восстановление из чекпоинта ring-20261106-195112.bin", "warn");
        logLine("freedos-lab: running");
        logLine("[revert] kolibri: Откат к снапшоту 0002-before-update", "");
    }
})();