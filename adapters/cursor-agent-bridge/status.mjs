#!/usr/bin/env node
/**
 * 检查 Cursor SDK 登录态（不输出密钥）。
 * 最终结果必须走 process.stdout.write；console.log 已重定向到 stderr。
 */
import { Cursor } from "@cursor/sdk";
import { existsSync } from "node:fs";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";

for (const level of ["log", "info", "debug", "warn"]) {
  console[level] = (...args) => console.error(...args);
}

function writeResult(obj) {
  process.stdout.write(JSON.stringify(obj));
}

const __dirname = dirname(fileURLToPath(import.meta.url));
const hasSdk = existsSync(join(__dirname, "node_modules", "@cursor", "sdk"));

if (!hasSdk) {
  writeResult({
    ok: false,
    status: "not-installed",
    hint: "请先运行 scripts/setup-cursor-agent.ps1",
  });
  process.exit(0);
}

const envKey = !!(process.env.CURSOR_API_KEY && process.env.CURSOR_API_KEY.trim());
const status = await Cursor.auth.status();

if (envKey) {
  writeResult({
    ok: true,
    status: "env-api-key",
    email: status.status === "logged-in" ? status.email : null,
    hint: "已检测到环境变量 CURSOR_API_KEY",
  });
  process.exit(0);
}

if (status.status === "logged-in") {
  writeResult({
    ok: true,
    status: "logged-in",
    email: status.email ?? null,
    apiKeyExpiresAtMs: status.apiKeyExpiresAtMs ?? null,
    hint: "本机 SDK 登录有效",
  });
  process.exit(0);
}

writeResult({
  ok: false,
  status: "logged-out",
  hint: "未登录。请运行 scripts/login-cursor-agent.bat 或 setup-cursor-agent.ps1 -Login；也可设置 CURSOR_API_KEY",
});
process.exit(0);
