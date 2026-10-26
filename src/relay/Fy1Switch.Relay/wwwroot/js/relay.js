const page = document.querySelector("main[data-page]")?.dataset.page;
const csrf = document.querySelector('meta[name="csrf-token"]')?.content;
const messages = {
    invalid_credentials: "登录失败, 请核对密码或 Passkey, 多次失败后请稍后再试.",
    invalid_csrf: "页面凭据已失效, 请刷新页面后重试.",
    password_invalid: "密码修改失败. 请核对当前密码, 新密码至少 12 位且包含大小写字母, 数字和符号.",
    passkey_invalid: "Passkey 验证失败, 请重新开始操作.",
    passkey_limit: "最多保存 8 个 Passkey, 请先移除不再使用的项目.",
    last_sign_in_method: "请保留至少一种可用的登录方式.",
    too_many_ceremonies: "当前请求较多, 请稍后再试.",
    device_not_found: "设备记录已更新, 请刷新列表.",
};

function notice(message = "", target = "notice", success = false) {
    const element = document.getElementById(target);
    if (!element) return;
    element.textContent = message;
    element.classList.toggle("success", success);
}

async function request(path, { method = "GET", body } = {}) {
    const headers = { Accept: "application/json", __RequestVerificationToken: csrf };
    if (body !== undefined) headers["Content-Type"] = "application/json";
    const response = await fetch(path, {
        method, headers, credentials: "same-origin", redirect: "error", cache: "no-store",
        ...(body !== undefined ? { body: JSON.stringify(body) } : {}),
    });
    let result;
    try { result = await response.json(); } catch { result = {}; }
    if (!response.ok) {
        if (response.status === 401 && page === "dashboard") {
            location.assign("/login");
            throw new Error("登录已过期, 请重新登录.");
        }
        throw new Error(messages[result.errorCode] ?? (response.status === 429
            ? "请求较多, 请稍后再试."
            : response.status === 401 ? messages.invalid_credentials : "操作未完成, 请检查连接后重试."));
    }
    return result;
}

async function busy(button, action, target = "notice") {
    if (button.disabled) return;
    button.disabled = true;
    notice("", target);
    try { await action(); }
    catch (error) { notice(error instanceof Error ? error.message : "操作未完成, 请重试.", target); }
    finally { button.disabled = false; }
}

function decodeBase64Url(value) {
    const text = atob(value.replace(/-/g, "+").replace(/_/g, "/"));
    return Uint8Array.from(text, character => character.charCodeAt(0)).buffer;
}

function encodeBase64Url(buffer) {
    return btoa(String.fromCharCode(...new Uint8Array(buffer))).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/g, "");
}

function creationOptions(options) {
    if (typeof PublicKeyCredential.parseCreationOptionsFromJSON === "function") return PublicKeyCredential.parseCreationOptionsFromJSON(options);
    return {
        ...options, challenge: decodeBase64Url(options.challenge),
        user: { ...options.user, id: decodeBase64Url(options.user.id) },
        excludeCredentials: options.excludeCredentials?.map(item => ({ ...item, id: decodeBase64Url(item.id) })),
    };
}

function requestOptions(options) {
    if (typeof PublicKeyCredential.parseRequestOptionsFromJSON === "function") return PublicKeyCredential.parseRequestOptionsFromJSON(options);
    return { ...options, challenge: decodeBase64Url(options.challenge), allowCredentials: options.allowCredentials?.map(item => ({ ...item, id: decodeBase64Url(item.id) })) };
}

function credentialJson(credential) {
    if (!credential) throw new Error("未完成 Passkey 验证, 请重试.");
    if (typeof credential.toJSON === "function") return JSON.stringify(credential.toJSON());
    const response = credential.response;
    const result = {
        id: credential.id, rawId: encodeBase64Url(credential.rawId), type: credential.type,
        authenticatorAttachment: credential.authenticatorAttachment,
        clientExtensionResults: credential.getClientExtensionResults(),
        response: { clientDataJSON: encodeBase64Url(response.clientDataJSON) },
    };
    for (const field of ["attestationObject", "authenticatorData", "signature", "userHandle"]) {
        if (response[field]) result.response[field] = encodeBase64Url(response[field]);
    }
    if (typeof response.getTransports === "function") result.response.transports = response.getTransports();
    return JSON.stringify(result);
}

function requirePasskey() {
    if (!window.isSecureContext || !window.PublicKeyCredential || !navigator.credentials) throw new Error("当前浏览器无法使用 Passkey, 请使用支持 WebAuthn 的浏览器通过 HTTPS 访问.");
}

async function usePasskey(create, name) {
    requirePasskey();
    try {
        if (create) {
            const options = await request("/api/account/passkeys/options", { method: "POST" });
            const credential = await navigator.credentials.create({ publicKey: creationOptions(options) });
            await request("/api/account/passkeys", { method: "POST", body: { credentialJson: credentialJson(credential), name } });
        } else {
            const options = await request("/api/account/passkey-login/options", { method: "POST" });
            const credential = await navigator.credentials.get({ publicKey: requestOptions(options) });
            await request("/api/account/passkey-login", { method: "POST", body: { credentialJson: credentialJson(credential) } });
        }
    } catch (error) {
        if (error instanceof DOMException) throw new Error("Passkey 操作未完成或已取消, 请重试.");
        throw error;
    }
}

if (page === "login") {
    const form = document.getElementById("login-form");
    form.addEventListener("submit", event => {
        event.preventDefault();
        const input = document.getElementById("login-password");
        const password = input.value;
        busy(form.querySelector("button"), async () => {
            await request("/api/account/login", { method: "POST", body: { password } });
            input.value = "";
            location.assign("/");
        });
    });
    const passkey = document.getElementById("passkey-login");
    passkey.addEventListener("click", () => busy(passkey, async () => { await usePasskey(false); location.assign("/"); }));
}

if (page === "dashboard") {
    const navigation = [...document.querySelectorAll("nav a")];
    function updateNavigation() {
        for (const link of navigation) {
            const active = link.hash === (location.hash || "#devices");
            link.classList.toggle("active", active);
            if (active) link.setAttribute("aria-current", "location"); else link.removeAttribute("aria-current");
        }
    }
    addEventListener("hashchange", updateNavigation);
    updateNavigation();
    let currentCode = null;
    const codeElement = document.getElementById("registration-code");
    const expiryElement = document.getElementById("code-expiry");
    const copyCode = document.getElementById("copy-code");
    const newCode = document.getElementById("new-code");
    const logout = document.getElementById("logout");
    const refresh = document.getElementById("refresh-devices");

    function updateExpiry() {
        if (!currentCode) return;
        const seconds = Math.max(0, Math.ceil((Date.parse(currentCode.expiresAt) - Date.now()) / 1000));
        expiryElement.textContent = seconds > 0 ? `有效期 ${Math.floor(seconds / 60).toString().padStart(2, "0")}:${(seconds % 60).toString().padStart(2, "0")}` : "接入码已过期, 请生成新码";
        copyCode.disabled = seconds <= 0;
        if (seconds <= 0) codeElement.textContent = "--- ---";
    }

    async function generateCode() {
        const result = await request("/api/admin/registration-code", { method: "POST" });
        if (!/^\d{6}$/.test(result.code) || !Number.isFinite(Date.parse(result.expiresAt))) throw new Error("接入码响应无效, 请重试.");
        currentCode = result;
        codeElement.textContent = `${result.code.slice(0, 3)} ${result.code.slice(3)}`;
        updateExpiry();
    }

    newCode.addEventListener("click", () => busy(newCode, generateCode));
    copyCode.addEventListener("click", () => busy(copyCode, async () => {
        if (!currentCode || Date.parse(currentCode.expiresAt) <= Date.now()) throw new Error("接入码已过期, 请生成新码.");
        try { await navigator.clipboard.writeText(currentCode.code); }
        catch { throw new Error("无法复制, 请手动输入页面中的接入码."); }
        notice("接入码已复制.", "notice", true);
    }));
    const copyAddress = document.getElementById("copy-address");
    copyAddress.addEventListener("click", () => busy(copyAddress, async () => {
        try { await navigator.clipboard.writeText(document.getElementById("relay-address").textContent); }
        catch { throw new Error("无法复制, 请手动输入页面中的 Relay 地址."); }
        notice("Relay 地址已复制.", "notice", true);
    }));
    logout.addEventListener("click", () => busy(logout, async () => { await request("/api/account/logout", { method: "POST" }); location.assign("/login"); }));

    async function loadDevices() {
        const list = document.getElementById("device-list");
        const devices = await request("/api/admin/devices");
        if (!Array.isArray(devices)) throw new Error("设备列表响应无效, 请重试.");
        list.closest("table").classList.toggle("has-devices", devices.length > 0);
        document.getElementById("table-scroll-hint").hidden = devices.length === 0;
        const rows = devices.map(device => {
            const row = document.createElement("tr");
            const name = document.createElement("td");
            name.textContent = device.deviceName;
            const id = document.createElement("td");
            id.className = "device-id";
            id.textContent = device.deviceId;
            const stateCell = document.createElement("td");
            const state = document.createElement("span");
            state.className = `connection-state${device.connected ? " online" : ""}`;
            state.textContent = device.connected ? "在线" : "离线";
            stateCell.append(state);
            const action = document.createElement("td");
            const remove = document.createElement("button");
            remove.type = "button";
            remove.className = "danger";
            remove.textContent = "移除";
            remove.setAttribute("aria-label", `移除 ${device.deviceName}`);
            remove.addEventListener("click", () => {
                if (!confirm(`移除 ${device.deviceName} 的 Relay 注册? 电脑与手机需要重新接入 Relay.`)) return;
                busy(remove, async () => {
                    await request(`/api/admin/devices/${encodeURIComponent(device.deviceId)}`, { method: "DELETE" });
                    await loadDevices();
                    notice("已移除电脑的 Relay 注册.", "notice", true);
                });
            });
            action.append(remove);
            row.append(name, id, stateCell, action);
            return row;
        });
        if (rows.length === 0) {
            const row = document.createElement("tr");
            const cell = document.createElement("td");
            cell.colSpan = 4;
            cell.className = "empty-state";
            cell.textContent = "尚未注册电脑. 在 Windows 端输入 Relay 地址和接入码即可连接.";
            row.append(cell);
            rows.push(row);
        }
        list.replaceChildren(...rows);
    }

    refresh.addEventListener("click", () => busy(refresh, loadDevices));
    const importLegacy = document.getElementById("import-legacy");
    importLegacy?.addEventListener("click", () => busy(importLegacy, async () => {
        const result = await request("/api/admin/import-legacy", { method: "POST" });
        await loadDevices();
        notice(`已导入 ${result.imported} 台电脑的旧配置. 已有和已撤销记录保持原状.`, "notice", true);
    }));
    const passwordForm = document.getElementById("password-form");
    const confirmation = document.getElementById("confirm-password");
    confirmation.addEventListener("input", () => confirmation.setCustomValidity(""));
    passwordForm.addEventListener("submit", event => {
        event.preventDefault();
        const newPassword = document.getElementById("new-password").value;
        if (newPassword !== confirmation.value) { confirmation.setCustomValidity("两次输入的新密码不一致."); confirmation.reportValidity(); return; }
        busy(passwordForm.querySelector("button"), async () => {
            await request("/api/account/password", { method: "POST", body: { currentPassword: document.getElementById("current-password").value, newPassword } });
            passwordForm.reset();
            notice("密码已修改, 其他浏览器的登录已失效.", "password-notice", true);
        }, "password-notice");
    });

    async function loadPasskeys() {
        const list = document.getElementById("passkey-list");
        const keys = await request("/api/account/passkeys");
        if (!Array.isArray(keys)) throw new Error("Passkey 列表响应无效, 请重试.");
        const rows = keys.map(key => {
            const row = document.createElement("li");
            const label = document.createElement("span");
            label.textContent = key.name;
            const remove = document.createElement("button");
            remove.className = "danger";
            remove.type = "button";
            remove.textContent = "移除";
            remove.setAttribute("aria-label", `移除 Passkey ${key.name}`);
            remove.addEventListener("click", () => {
                if (!confirm(`移除 Passkey ${key.name}?`)) return;
                busy(remove, async () => {
                    await request(`/api/account/passkeys/${encodeURIComponent(key.id)}`, { method: "DELETE" });
                    await loadPasskeys();
                    notice("Passkey 已移除.", "passkey-notice", true);
                }, "passkey-notice");
            });
            row.append(label, remove);
            return row;
        });
        if (rows.length === 0) {
            const row = document.createElement("li");
            row.className = "muted";
            row.textContent = "当前未添加 Passkey.";
            rows.push(row);
        }
        list.replaceChildren(...rows);
    }

    const addPasskey = document.getElementById("add-passkey");
    addPasskey.addEventListener("click", () => busy(addPasskey, async () => {
        const name = document.getElementById("passkey-name").value.trim();
        if (!name || name.length > 80 || /[\x00-\x1f\x7f]/.test(name)) throw new Error("请填写 1 至 80 个字符的 Passkey 名称.");
        await usePasskey(true, name);
        await loadPasskeys();
        notice("Passkey 已添加.", "passkey-notice", true);
    }, "passkey-notice"));

    busy(newCode, generateCode);
    busy(refresh, loadDevices);
    loadPasskeys().catch(error => notice(error.message, "passkey-notice"));
    setInterval(updateExpiry, 1000);
    setInterval(() => { if (!document.hidden) loadDevices().catch(error => notice(error.message)); }, 20000);
}
