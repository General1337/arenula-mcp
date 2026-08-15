#!/usr/bin/env node
// Check that every MCP tool's advertised inputSchema matches what its handler actually
// reads. Exits non-zero on a mismatch.
//
// Why this exists: on 2026-08-15 an audit found 110 parameters that handlers read but no
// schema declared, across 8 of the 23 tools. Every tool sets additionalProperties:false,
// so an undeclared parameter is invisible to a model reading the schema and rejected
// outright by a validating client — the capability may as well not exist. The reverse
// case (declared but never read) advertises a no-op. Neither shows up at compile time,
// neither throws at runtime, and both are silent to anyone not diffing the two by hand.
//
//   node scripts/check-tool-contracts.mjs
//
// Known-good exceptions live in ALLOWED_IGNORED below: a handful of parameters are read
// somewhere other than their own handler (the dispatcher routes editor.console_run to
// ConsoleHandler) or out of a nested object rather than `args` (camera shots carry their
// own label). Those are correct, so they are listed rather than "fixed".

import { readFileSync, readdirSync } from 'node:fs';
import { join, dirname, basename } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..', 'editor', 'Editor');
const REGISTRY = join(ROOT, 'Core', 'ToolRegistry.cs');
const DISPATCHER = join(ROOT, 'Core', 'RpcDispatcher.cs');

// tool -> params legitimately declared but not read by that tool's own handler.
const ALLOWED_IGNORED = {
	editor: ['command'],  // console_run/console_list are dispatched to ConsoleHandler
	camera: ['label'],    // read from each element of the `shots` array, not from args
};

const read = (p) => readFileSync(p, 'utf8');

function toolHandlerMap() {
	const src = read(DISPATCHER);
	const map = new Map();
	for (const m of src.matchAll(/"(\w+)"\s*=>\s*(\w+)\.Handle/g)) map.set(m[1], m[2]);
	// compile/cloud/schema dispatch async, before the MainThread await, in if/else form.
	for (const m of src.matchAll(/toolName == "(\w+)"[\s\S]{0,200}?(\w+Handler)\.HandleAsync/g)) {
		map.set(m[1], m[2]);
	}
	return map;
}

function toolBlocks() {
	const src = read(REGISTRY);
	const marks = [...src.matchAll(/name = "(\w+)"/g)].map((m) => [m.index, m[1]]);
	const out = new Map();
	marks.forEach(([pos, tool], i) => {
		out.set(tool, src.slice(pos, i + 1 < marks.length ? marks[i + 1][0] : src.length));
	});
	return out;
}

function handlerSources() {
	const dir = join(ROOT, 'Handlers');
	const out = new Map();
	for (const f of readdirSync(dir)) {
		if (f.endsWith('.cs')) out.set(basename(f, '.cs'), read(join(dir, f)));
	}
	return out;
}

// Every way a handler can pull a named value out of `args`. Add to this if HandlerBase
// grows another accessor, or the check silently under-reports.
function paramsRead(src) {
	const names = new Set();
	const patterns = [
		/\b(?:Try)?Get(?:String|Int|Bool|Float|AssetPath)\s*\(\s*args\s*,\s*"([^"]+)"/g,
		/\bApplyColor\s*\(\s*args\s*,\s*"([^"]+)"/g,
		/\bargs\s*\.\s*(?:Try)?GetProperty\s*\(\s*"([^"]+)"/g,
	];
	for (const rx of patterns) for (const m of src.matchAll(rx)) names.add(m[1]);
	if (/\bGetAssetPath\s*\(\s*args\s*\)/.test(src)) names.add('path');
	names.delete('action');
	return names;
}

const handlers = handlerSources();
const blocks = toolBlocks();
let unreachable = 0;
let ignored = 0;

for (const [tool, handlerClass] of [...toolHandlerMap()].sort()) {
	const block = blocks.get(tool);
	const src = handlers.get(handlerClass);
	if (!block || !src) {
		console.error(`FAIL ${tool}: no ${!block ? 'registry block' : 'handler source'}`);
		process.exitCode = 1;
		continue;
	}
	const declared = new Set([...block.matchAll(/\["([^"]+)"\]\s*=\s*new\b/g)].map((m) => m[1]));
	declared.delete('action');
	const allowed = new Set(ALLOWED_IGNORED[tool] ?? []);

	const missing = [...paramsRead(src)].filter((n) => !declared.has(n)).sort();
	const dead = [...declared].filter((n) => !paramsRead(src).has(n) && !allowed.has(n)).sort();

	if (missing.length) {
		console.error(`UNREACHABLE ${tool}: read by ${handlerClass} but not declared — ${missing.join(', ')}`);
		unreachable += missing.length;
	}
	if (dead.length) {
		console.error(`IGNORED     ${tool}: declared but never read — ${dead.join(', ')}`);
		ignored += dead.length;
	}
}

if (unreachable || ignored) {
	console.error(`\n${unreachable} unreachable, ${ignored} ignored.`);
	console.error('Declare the missing params in ToolRegistry.cs, or remove the dead ones.');
	console.error('If a param is genuinely read elsewhere, add it to ALLOWED_IGNORED with a reason.');
	process.exitCode = 1;
} else {
	console.log(`tool contracts OK — ${blocks.size} tools, schema matches handlers`);
}
