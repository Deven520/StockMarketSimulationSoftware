#!/usr/bin/env node
/**
 * 用浏览器登录当前 Cursor 账号，并把 SDK API Key 存到本机（~/.cursor/sdk/）。
 * 不收集、不保存账号密码；登录在 Cursor 官网完成。
 */
import { Cursor } from "@cursor/sdk";

console.error("正在打开浏览器登录页…请用当前 Cursor 账号完成授权。");

const result = await Cursor.auth.login({
  apiKeyName: "StockSim Local Bridge",
  openBrowser: true,
  onLoginUrl: (url) => {
    console.error("若浏览器未自动打开，请手动访问：");
    console.error(url);
  },
});

console.log(
  JSON.stringify(
    {
      ok: true,
      email: result.email ?? null,
      apiKeyExpiresAtMs: result.apiKeyExpiresAtMs ?? null,
      note: "已写入本机 SDK 凭证。勿把密钥提交到 Git。请重启 StockSim.Web 后再试选股。",
    },
    null,
    2
  )
);
