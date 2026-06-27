import { appendFile, mkdir, readFile, stat, writeFile } from "node:fs/promises";
import { existsSync } from "node:fs";
import { createHash } from "node:crypto";
import { dirname, resolve } from "node:path";
import { spawn } from "node:child_process";
import { fileURLToPath } from "node:url";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const input = process.env.COZY_TEXT_TSV || resolve(root, "learning_text", "cozyisland_text_extract.tsv");
const outputDir = process.env.MIMO_CACHE_DIR || resolve(root, "audio_cache", "mimo", "zh-CN");
const tmpDir = resolve(root, "tmp", "mimo-cache");
const python = process.env.PYTHON_PATH || "C:/Users/yangmie/.cache/codex-runtimes/codex-primary-runtime/dependencies/python/python.exe";
const mimoScript = process.env.MIMO_TTS_SCRIPT || "C:/Users/yangmie/.codex/skills/mimo-v2-5-tts/scripts/mimo_tts.py";
const voice = process.env.MIMO_VOICE || "冰糖";
const batchLimit = Number(process.env.MIMO_BATCH_LIMIT || 20);
const concurrency = Math.max(1, Number(process.env.MIMO_CONCURRENCY || 1));
const sleepMs = Number(process.env.MIMO_SLEEP_MS || 1200);
const startGapMs = Math.max(0, Number(process.env.MIMO_START_GAP_MS || 0));
const scope = process.env.MIMO_SCOPE || "dialogue";
const dryRun = process.env.MIMO_DRY_RUN === "1";
const context = process.env.MIMO_CONTEXT ||
  "儿童学习游戏的剧情、告示牌和制作说明朗读。声音亲切、清楚、耐听，像温柔的小老师陪孩子读游戏文字；语速自然稍慢，重点词读准，保留一点探索感，不夸张，不喊叫，不唱读。";

function run(command, args) {
  return new Promise((resolveRun, reject) => {
    const child = spawn(command, args, {
      stdio: ["ignore", "pipe", "pipe"],
      env: process.env
    });
    let stdout = "";
    let stderr = "";
    child.stdout.on("data", (chunk) => {
      stdout += chunk.toString();
    });
    child.stderr.on("data", (chunk) => {
      stderr += chunk.toString();
    });
    child.on("exit", (code) => {
      if (code === 0) {
        resolveRun({ stdout, stderr });
      } else {
        reject(new Error(`${command} exited with ${code}: ${stderr || stdout}`));
      }
    });
  });
}

function sleep(ms) {
  return new Promise((resolveSleep) => setTimeout(resolveSleep, ms));
}

function sha1(text) {
  return createHash("sha1").update(text, "utf8").digest("hex");
}

function countChinese(text) {
  let count = 0;
  for (const char of text) {
    const code = char.codePointAt(0);
    if (code >= 0x4e00 && code <= 0x9fff) count += 1;
  }
  return count;
}

function parseTsv(content) {
  const lines = content.replace(/^\uFEFF/, "").split(/\r?\n/).filter(Boolean);
  const rows = [];
  for (const line of lines.slice(1)) {
    const cols = line.split("\t");
    if (cols.length < 2) continue;
    rows.push({
      category: cols[0],
      zh: cols[1],
      english: cols[2] || ""
    });
  }
  return rows;
}

function normalizeForCache(text) {
  return text
    .replace(/\\n/g, " ")
    .replace(/\r?\n/g, " ")
    .replace(/\s+/g, " ")
    .trim();
}

function hasTooMuchNoise(text) {
  const punctuationNoise = (text.match(/[<>_=\-]{3,}/g) || []).length;
  return punctuationNoise > 2;
}

function isDialogueCandidate(row) {
  const text = normalizeForCache(row.zh || "");
  if (!text) return false;
  if (!["任务/句子", "界面/任务", "地点/工具", "配方/蓝图"].includes(row.category)) return false;
  if (text.length < 18 || text.length > 180) return false;
  if (countChinese(text) < 14) return false;

  const rejectPattern = /奖励列表|未绑定|Steam|Demo|Bilibili|抖音|小红书|qq|QQ群|时间\s*<|金牌|银牌|铜牌|扭蛋|奖池|按键|键盘|鼠标|左键|右键|ESC|WASD|TAB|空格|右下角|操作提示|建造提示|检测到画面|画面选项|下载地区|VPN|加速器|代理|^-|----|\[|\]|\/|\\/i;
  if (rejectPattern.test(text)) return false;

  if (hasTooMuchNoise(text)) return false;

  return true;
}

function isBroadCandidate(row) {
  const text = normalizeForCache(row.zh || "");
  if (!text) return false;
  if (!["任务/句子", "界面/任务", "地点/工具", "配方/蓝图", "物品/短词"].includes(row.category)) return false;
  if (text.length < 2 || text.length > 180) return false;
  if (countChinese(text) < 2) return false;

  const rejectPattern = /Steam|Demo|Bilibili|Blibili|抖音|小红书|QQ群|下载地区|VPN|加速器|代理|^-|----|\[数字|未绑定|时间\s*<|金牌|银牌|铜牌/i;
  if (rejectPattern.test(text)) return false;
  if (hasTooMuchNoise(text)) return false;

  return true;
}

function isAllCandidate(row) {
  const text = normalizeForCache(row.zh || "");
  if (!text) return false;
  if (text.length < 2 || text.length > 220) return false;
  if (countChinese(text) < 1) return false;

  const rejectPattern = /SteamID|^-{3,}$|^\d+$/i;
  if (rejectPattern.test(text)) return false;
  if (hasTooMuchNoise(text)) return false;

  return true;
}

function isMimoCandidate(row) {
  if (scope === "all") return isAllCandidate(row);
  if (scope === "broad") return isBroadCandidate(row);
  return isDialogueCandidate(row);
}

function selectCandidates(rows) {
  const seen = new Set();
  return rows
    .map((row) => ({ ...row, text: normalizeForCache(row.zh || "") }))
    .filter(isMimoCandidate)
    .filter((row) => {
      if (seen.has(row.text)) return false;
      seen.add(row.text);
      return true;
    })
    .sort((a, b) => {
      const categoryWeight = (category) => {
        if (category === "任务/句子") return 4;
        if (category === "地点/工具") return 3;
        if (category === "配方/蓝图") return 2;
        if (category === "物品/短词") return scope === "dialogue" ? 0 : 2;
        if (category === "界面/任务") return scope === "dialogue" ? 1 : 3;
        return 1;
      };
      const categoryCompare = categoryWeight(b.category) - categoryWeight(a.category);
      if (categoryCompare !== 0) return categoryCompare;
      if (scope === "dialogue") return b.text.length - a.text.length;
      return a.text.length - b.text.length;
    });
}

async function fileSize(path) {
  try {
    return (await stat(path)).size;
  } catch {
    return 0;
  }
}

async function main() {
  if (!existsSync(input)) throw new Error(`Text TSV not found: ${input}`);
  if (!existsSync(python)) throw new Error(`Python not found: ${python}`);
  if (!existsSync(mimoScript)) throw new Error(`MiMo script not found: ${mimoScript}`);
  if (!process.env.MIMO_API_KEY && !dryRun) {
    throw new Error("MIMO_API_KEY is missing. Set it before generating audio.");
  }

  await mkdir(outputDir, { recursive: true });
  await mkdir(tmpDir, { recursive: true });

  const rows = parseTsv(await readFile(input, "utf8"));
  const candidates = selectCandidates(rows);
  const missing = [];
  for (const candidate of candidates) {
    const hash = sha1(candidate.text);
    const wavPath = resolve(outputDir, `${hash}.wav`);
    if (!existsSync(wavPath) || await fileSize(wavPath) <= 44) {
      missing.push({ ...candidate, hash, wavPath, textPath: resolve(outputDir, `${hash}.txt`) });
    }
  }

  const selected = missing.slice(0, batchLimit);
  await writeFile(resolve(tmpDir, "selected-mimo-dialogue.json"), JSON.stringify(selected.map(({ wavPath, textPath, ...item }) => item), null, 2), "utf8");

  if (dryRun) {
    console.log(`MiMo dry run (${scope}): ${selected.length}/${missing.length} selected, ${candidates.length} candidates.`);
    return;
  }

  await writeFile(resolve(tmpDir, "generated.jsonl"), "", "utf8");
  await writeFile(resolve(tmpDir, "failed.jsonl"), "", "utf8");

  const generated = [];
  const failed = [];
  let nextIndex = 0;
  let nextStartAt = Date.now();

  async function waitForStartSlot() {
    if (startGapMs <= 0) return;
    const now = Date.now();
    const waitMs = Math.max(0, nextStartAt - now);
    nextStartAt = Math.max(nextStartAt, now) + startGapMs;
    if (waitMs > 0) {
      await sleep(waitMs);
    }
  }

  async function worker() {
    while (true) {
      const index = nextIndex;
      nextIndex += 1;
      if (index >= selected.length) return;

      const job = selected[index];
      try {
        await writeFile(job.textPath, job.text, "utf8");
        await waitForStartSlot();
        await run(python, [
          mimoScript,
          "--voice", voice,
          "--context", context,
          "--text", job.text,
          "--output", job.wavPath
        ]);
        generated.push(job);
        await appendFile(resolve(tmpDir, "generated.jsonl"), `${JSON.stringify({ hash: job.hash, category: job.category, text: job.text, wav: job.wavPath })}\n`, "utf8");
        await sleep(sleepMs);
      } catch (error) {
        failed.push({ ...job, error: error.message });
        await appendFile(resolve(tmpDir, "failed.jsonl"), `${JSON.stringify({ hash: job.hash, category: job.category, text: job.text, error: error.message })}\n`, "utf8");
      }
    }
  }

  const workerCount = Math.min(concurrency, Math.max(selected.length, 1));
  await Promise.all(Array.from({ length: workerCount }, () => worker()));

  const index = candidates.map((candidate) => {
    const hash = sha1(candidate.text);
    return {
      hash,
      category: candidate.category,
      text: candidate.text,
      wav: `${hash}.wav`,
      txt: `${hash}.txt`,
      ready: existsSync(resolve(outputDir, `${hash}.wav`))
    };
  });
  await writeFile(resolve(outputDir, "index.json"), JSON.stringify(index, null, 2), "utf8");

  const summary = {
    generatedAt: new Date().toISOString(),
    scope,
    voice,
    batchLimit,
    concurrency: workerCount,
    startGapMs,
    selected: selected.length,
    generated: generated.length,
    failed: failed.length,
    candidates: candidates.length,
    remainingBeforeRun: missing.length
  };
  await writeFile(resolve(tmpDir, "summary.json"), JSON.stringify(summary, null, 2), "utf8");
  console.log(`MiMo cache done (${scope}, concurrency ${workerCount}): ${generated.length}/${selected.length} generated, ${failed.length} failed.`);
}

main().catch((error) => {
  console.error(error.message);
  process.exit(1);
});
