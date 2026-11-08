#!/usr/bin/env node
/**
 * StockSim Cursor Agent 桥接：从 stdin 读任务 JSON，调用本地 Agent.prompt，向 stdout 写结果 JSON。
 *
 * 输入示例：
 *   { "type":"StockPick", "prompt":"...", "model":"composer-2.5", "cwd":"...", "apiKey":"..." }
 *
 * 认证优先级：输入 apiKey → CURSOR_API_KEY → Cursor.auth 本地登录态（login.mjs）
 * 不支持、也不应传入 Cursor 账号密码。
 */
import { Agent, CursorAgentError } from "@cursor/sdk";
import { readFileSync } from "node:fs";

// SDK 会把 INFO 日志打到 stdout（如 "17:34:31.848 INFO ..."），污染 JSON。
// 统一把 log/info/debug/warn 转到 stderr，保证 stdout 仅最终 JSON。
for (const level of ["log", "info", "debug", "warn"]) {
  console[level] = (...args) => console.error(...args);
}

function readStdin() {
  try {
    return readFileSync(0, "utf8");
  } catch {
    return "";
  }
}

function extractJsonObject(text) {
  if (!text || typeof text !== "string") return null;
  const trimmed = text.trim();
  try {
    return JSON.parse(trimmed);
  } catch {
    /* continue */
  }

  const fence = trimmed.match(/```(?:json)?\s*([\s\S]*?)```/i);
  if (fence) {
    try {
      return JSON.parse(fence[1].trim());
    } catch {
      /* continue */
    }
  }

  const start = trimmed.indexOf("{");
  const end = trimmed.lastIndexOf("}");
  if (start >= 0 && end > start) {
    try {
      return JSON.parse(trimmed.slice(start, end + 1));
    } catch {
      return null;
    }
  }
  return null;
}

function buildStockPickPrompt(userPrompt, payloadJson) {
  return `你是 A 股模拟做T系统的选股助手。根据用户提示词给出候选股票，必须严格只输出一个 JSON 对象（不要 Markdown，不要解释），字段如下：
{
  "analysis": "中文分析说明，多行可含换行",
  "steps": ["步骤1", "步骤2", "步骤3"],
  "items": [
    {
      "symbol": "6位A股代码",
      "name": "中文简称",
      "reason": "一句话入选理由",
      "logic": "该股选取逻辑：为何匹配目标收益/周期/题材、流动性与做T适配性（可多句）",
      "risk": "主要风险"
    }
  ],
  "note": "可选备注"
}
约束：
- items 1~5 只真实可交易的 A 股（沪深主板/创业板/科创板），不要港股美股
- symbol 仅数字代码（如 600519），不要带交易所前缀
- 每只股票必须填写 logic（选取逻辑），便于历史查阅
- 紧扣用户提示词与目标上下文（预期收益、周期、题材），不要固定输出招商银行/五粮液除非提示词确实要求
- 这是模拟建议，非投资建议

用户提示词：
${userPrompt}

目标/上下文 JSON（若有请一并遵守）：
${payloadJson || "{}"}`;
}

function buildGoalToPrompt(userPrompt, payloadJson) {
  return `你是 A 股模拟做T系统的提示词工程师。用户给出了投资目标，请生成「下一步选股模型」要用的中文提示词。
必须严格只输出一个 JSON 对象（不要 Markdown），字段：
{
  "prompt": "给选股模型用的完整中文提示词",
  "rationale": "为何这样写提示词",
  "analysis": "简要说明如何把目标落到选股约束"
}
重要区分（极易搞错，请仔细读）：
1) 你本步的 JSON 回复里不要直接列股票代码——本步只产出 prompt/rationale/analysis。
2) 但字段 prompt 的内容必须要求「下一步选股模型」输出 3~5 只具体 A 股（含 6 位代码 symbol、名称 name、理由 reason、选取逻辑 logic、风险 risk）。
3) prompt 中严禁出现「不要输出股票代码」「不要列代码」「只给行业方向不给标的」之类禁止列股票的句子。
4) prompt 必须显式写入 expectedReturnPct、horizonDays、themePreference（可用中文改写，数字与题材不能丢）。
5) 面向 A 股模拟日内做T：流动性、波动、可交易性，避免 ST。

上层说明：
${userPrompt}

投资目标 JSON：
${payloadJson || "{}"}`;
}

function buildStrategyPrompt(userPrompt, payloadJson) {
  return `你是 A 股模拟做T策略顾问。上下文可能包含：单只股票近21日日K、报价估值、热点新闻、个股新闻、当前参数、当日成交。
请给出（或迭代更新）该股票的专属日内做T参数。必须严格只输出一个 JSON 对象（不要 Markdown），字段如下：
{
  "analysis": "中文分析说明（结合行情与新闻）",
  "steps": ["步骤1", "步骤2"],
  "parameters": {
    "mode": "IntradayT",
    "buyTriggerPct": -0.6,
    "sellTriggerPct": 0.7,
    "positionPctPerTrade": 0.15,
    "maxTradesPerDay": 6,
    "stopLossPct": -1.2,
    "takeProfitPct": 1.0,
    "tradingSession": "ContinuousAuction",
    "feeModel": "AShareDefault"
  },
  "changeNote": "变更说明",
  "risk": "风险提示"
}
约束：
- 参数须合理，贴合该股波动与流动性；高波动可略放宽触发阈值、收紧仓位
- 不得建议绕过涨跌停/资金硬风控
- 这是模拟建议，非投资建议
- 若上下文含 currentParameters，请在其基础上迭代而非无视

用户目标：
${userPrompt}

当前上下文 JSON：
${payloadJson || "{}"}`;
}

async function main() {
  const raw = readStdin();
  if (!raw.trim()) {
    console.error("stdin 为空：需要任务 JSON");
    process.exit(2);
  }

  let task;
  try {
    task = JSON.parse(raw);
  } catch (e) {
    console.error("任务 JSON 解析失败: " + e.message);
    process.exit(2);
  }

  const type = task.type || "StockPick";
  const prompt = String(task.prompt || "").trim();
  if (!prompt) {
    console.error("缺少 prompt");
    process.exit(2);
  }

  const modelId = task.model || process.env.STOCKSIM_CURSOR_MODEL || "composer-2.5";
  const cwd = task.cwd || process.cwd();
  // 空字符串视为未设置，避免覆盖本机 Cursor.auth.login 凭证
  const rawKey = (task.apiKey || process.env.CURSOR_API_KEY || "").trim();
  const apiKey = rawKey || undefined;

  if (!apiKey) {
    try {
      const { Cursor } = await import("@cursor/sdk");
      const st = await Cursor.auth.status();
      if (st.status !== "logged-in") {
        console.error(
          JSON.stringify({
            ok: false,
            error: "未配置 Cursor 凭证：无 CURSOR_API_KEY，且本机未 Login",
            hint: "请双击 scripts/login-cursor-agent.bat，或运行 setup-cursor-agent.ps1 -Login",
          })
        );
        process.exit(4);
      }
    } catch (e) {
      console.error(
        JSON.stringify({
          ok: false,
          error: "无法检查 Cursor 登录态: " + (e?.message || e),
          hint: "请运行 scripts/setup-cursor-agent.ps1 -Login",
        })
      );
      process.exit(4);
    }
  }

  let message;
  if (type === "StrategyUpdate") {
    message = buildStrategyPrompt(prompt, task.payloadJson);
  } else if (type === "GoalToPrompt") {
    message = buildGoalToPrompt(prompt, task.payloadJson);
  } else {
    message = buildStockPickPrompt(prompt, task.payloadJson);
  }

  const options = {
    model: { id: modelId },
    // 文本推理即可，禁止改仓库文件
    tools: [],
    local: { cwd },
  };
  if (apiKey) options.apiKey = apiKey;

  try {
    // 无显式 key 时 SDK 会使用 Cursor.auth.login 写入的本机凭证
    const result = await Agent.prompt(message, options);
    if (result.status !== "finished") {
      console.error(
        JSON.stringify({
          ok: false,
          status: result.status,
          error: result.error?.message || "run failed",
          runId: result.id,
        })
      );
      process.exit(3);
    }

    const text = result.result || "";
    let parsed = extractJsonObject(text);
    if (!parsed) {
      parsed = {
        analysis: text || "Agent 未返回可解析 JSON，已保留原文。",
        steps: ["调用 Cursor Agent", "解析响应失败，回传原文"],
        items: type === "StockPick" ? [] : undefined,
        parameters: type === "StrategyUpdate" ? undefined : undefined,
        note: "raw_unparsed",
        raw: text,
      };
      if (type === "StrategyUpdate") {
        parsed.changeNote = "未能解析策略 JSON，请人工审阅 raw";
        parsed.risk = "输出格式异常";
      }
      if (type === "GoalToPrompt") {
        parsed.prompt = text || "";
        parsed.rationale = "Agent 未返回可解析 JSON，已保留原文为 prompt";
      }
    }

    parsed._meta = {
      provider: "CursorSdk",
      model: modelId,
      runId: result.id,
      durationMs: result.durationMs,
      status: result.status,
    };

    process.stdout.write(JSON.stringify(parsed, null, 2));
  } catch (err) {
    const messageText = err instanceof CursorAgentError ? err.message : String(err?.message || err);
    const retryable = err instanceof CursorAgentError ? !!err.isRetryable : false;
    const invalidKey = /invalid.*api.?key/i.test(messageText);
    console.error(
      JSON.stringify({
        ok: false,
        error: messageText,
        retryable,
        hint: invalidKey
          ? "API Key 无效或过期：请重新运行 scripts/login-cursor-agent.bat，或在 Dashboard → Integrations 创建新 Key 并设置 CURSOR_API_KEY"
          : "请运行 scripts/login-cursor-agent.bat，或设置 CURSOR_API_KEY（https://cursor.com/dashboard/integrations）",
      })
    );
    process.exit(invalidKey ? 5 : 1);
  }
}

main();
