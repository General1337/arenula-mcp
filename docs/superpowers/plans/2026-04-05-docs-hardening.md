# Docs Hardening Pass — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix all known bugs and robustness gaps in the arenula-docs MCP server without changing its architecture or feature surface.

**Architecture:** 8 files across 3 layers (schemas, libs, tools). All fixes are localized — no new files, no new dependencies, no structural changes.

**Tech Stack:** TypeScript, MiniSearch, Cheerio, Turndown

---

### Task 1: Fix LRU cache — refresh access order on `get()`

**Files:**
- Modify: `docs/src/lib/cache.ts:16-24`

The current `get()` method reads an entry but doesn't move it to the end of the Map's insertion order. This means "LRU eviction" actually evicts "first inserted" rather than "least recently used". The fix: delete and re-insert the entry on access so it moves to the end.

- [ ] **Step 1: Fix `get()` to refresh insertion order**

In `docs/src/lib/cache.ts`, replace the `get` method:

```typescript
get(key: string): T | undefined {
  const entry = this.store.get(key)
  if (!entry) return undefined
  if (Date.now() > entry.expiresAt) {
    this.store.delete(key)
    return undefined
  }
  // Refresh insertion order for LRU — delete and re-insert
  this.store.delete(key)
  this.store.set(key, entry)
  return entry.value
}
```

- [ ] **Step 2: Build to verify**

Run: `cd docs && npm run build`
Expected: Clean compilation, no errors.

- [ ] **Step 3: Commit**

```bash
git add docs/src/lib/cache.ts
git commit -m "fix(docs): LRU cache now refreshes access order on get()"
```

---

### Task 2: Fix snippet extraction bug in search-index

**Files:**
- Modify: `docs/src/lib/search-index.ts:76-93`

Two bugs: (1) when the query term isn't found (`matchIdx === -1`), `snippetStart` becomes 0 and returns a random snippet from the start. (2) Only the first word of multi-word queries is used for matching.

- [ ] **Step 1: Fix the `search()` function's snippet extraction**

In `docs/src/lib/search-index.ts`, replace the `search` function:

```typescript
export function search(query: string, limit = 10): SearchResult[] {
  const results = index.search(query).slice(0, limit)
  return results.map(r => {
    const content = (r as unknown as { content: string }).content || ''

    // Try each query word to find the best snippet anchor
    const lowerContent = content.toLowerCase()
    const queryWords = query.toLowerCase().split(/\s+/).filter(Boolean)
    let matchIdx = -1
    for (const word of queryWords) {
      matchIdx = lowerContent.indexOf(word)
      if (matchIdx !== -1) break
    }

    let snippet: string
    if (matchIdx !== -1) {
      const snippetStart = Math.max(0, matchIdx - 50)
      snippet = content.slice(snippetStart, snippetStart + 200).trim()
    } else {
      // No match in content — use the beginning as a fallback
      snippet = content.slice(0, 200).trim()
    }

    return {
      title: r.title as string,
      url: r.url as string,
      snippet: snippet ? `...${snippet}...` : '',
    }
  })
}
```

- [ ] **Step 2: Build to verify**

Run: `cd docs && npm run build`
Expected: Clean compilation, no errors.

- [ ] **Step 3: Commit**

```bash
git add docs/src/lib/search-index.ts
git commit -m "fix(docs): snippet extraction handles missing matches and multi-word queries"
```

---

### Task 3: Harden section filtering in get-api-type

**Files:**
- Modify: `docs/src/tools/get-api-type.ts:31-58`

The current section filter uses `lower.includes('## method')` which can false-positive on content inside code blocks. Fix: only match lines that start with `#` headings, and use a tighter regex.

- [ ] **Step 1: Replace the section filtering logic**

In `docs/src/tools/get-api-type.ts`, replace the filtering block (lines 31-58):

```typescript
  // Filter sections if requested
  let content = markdown
  if (!include_methods || !include_properties) {
    const lines = content.split('\n')
    const filtered: string[] = []
    let inMethodSection = false
    let inPropertySection = false
    let inCodeBlock = false

    for (const line of lines) {
      // Track fenced code blocks to avoid matching headings inside them
      if (line.startsWith('```')) {
        inCodeBlock = !inCodeBlock
      }

      if (!inCodeBlock && /^#{2,3}\s/i.test(line)) {
        const lower = line.toLowerCase()
        if (/^#{2,3}\s+methods?\b/i.test(line)) {
          inMethodSection = true
          inPropertySection = false
        } else if (/^#{2,3}\s+propert(y|ies)\b/i.test(line)) {
          inPropertySection = true
          inMethodSection = false
        } else {
          inMethodSection = false
          inPropertySection = false
        }
      }

      const skip =
        (inMethodSection && !include_methods) ||
        (inPropertySection && !include_properties)

      if (!skip) filtered.push(line)
    }
    content = filtered.join('\n')
  }
```

- [ ] **Step 2: Build to verify**

Run: `cd docs && npm run build`
Expected: Clean compilation, no errors.

- [ ] **Step 3: Commit**

```bash
git add docs/src/tools/get-api-type.ts
git commit -m "fix(docs): section filtering skips code blocks, uses tighter heading regex"
```

---

### Task 4: Improve fetcher body fallback

**Files:**
- Modify: `docs/src/lib/fetcher.ts:30-48`

When no content selector matches, the fallback grabs the entire `<body>` including residual nav/sidebar junk. Fix: strip more non-content elements before the fallback, and add a length check so empty extractions don't silently pass through.

- [ ] **Step 1: Harden the content extraction**

In `docs/src/lib/fetcher.ts`, replace lines 30-48 (from the `$('nav, ...')` removal through the rawHtml fallback):

```typescript
  // Remove non-content elements
  $('nav, .sidebar, footer, .header, .nav, script, style, .breadcrumb, .toc, .menu, .toolbar, .pagination, iframe, noscript').remove()

  const title = $('h1').first().text().trim() || $('title').text().trim() || 'Untitled'

  // Try multiple content selectors — Facepunch docs use different layouts
  const contentSelectors = ['.document-content', '.content', '.article-content', 'main', 'article', '.page-content']
  let rawHtml = ''
  for (const selector of contentSelectors) {
    const el = $(selector).first()
    if (el.length > 0) {
      rawHtml = el.html() || ''
      break
    }
  }
  if (!rawHtml) {
    rawHtml = $('body').html() || ''
  }

  const markdown = turndown.turndown(rawHtml).trim()
  if (!markdown) {
    throw new Error(`No content extracted from ${url}`)
  }
```

- [ ] **Step 2: Build to verify**

Run: `cd docs && npm run build`
Expected: Clean compilation, no errors.

- [ ] **Step 3: Commit**

```bash
git add docs/src/lib/fetcher.ts
git commit -m "fix(docs): strip more non-content elements, throw on empty extraction"
```

---

### Task 5: Add error handling to main() and ensureInitialized

**Files:**
- Modify: `docs/src/index.ts:45-50`
- Modify: `docs/src/tools/search-docs.ts:5-8`

Two gaps: (1) `main().catch(console.error)` swallows fatal startup errors silently — should exit with code 1. (2) `searchDocs` doesn't handle `ensureInitialized` failures — if all seed URLs fail, the error bubbles up as an unhandled MCP error.

- [ ] **Step 1: Fix main() error handling**

In `docs/src/index.ts`, replace the `main` function and its invocation (lines 45-50):

```typescript
async function main() {
  const transport = new StdioServerTransport()
  await server.connect(transport)
  console.error('[arenula-docs] Server running on stdio')
}

main().catch(err => {
  console.error('[arenula-docs] Fatal error:', err)
  process.exit(1)
})
```

- [ ] **Step 2: Add error handling to searchDocs**

In `docs/src/tools/search-docs.ts`, wrap the `ensureInitialized` call:

```typescript
export async function searchDocs(params: SearchDocsParams): Promise<string> {
  // Lazy-initialize the search index on first query
  try {
    await ensureInitialized(fetchPage)
  } catch (err) {
    console.error('[arenula-docs] Search index initialization failed:', err)
    // Continue with whatever was indexed — partial results are better than none
  }

  const results = search(params.query, params.limit)

  if (results.length === 0) {
    return JSON.stringify({
      query: params.query,
      count: 0,
      results: [],
      hint: 'No indexed results. Try sbox_docs_get_page with a direct URL from docs.facepunch.com.',
    })
  }

  return JSON.stringify({
    query: params.query,
    count: results.length,
    results,
  })
}
```

- [ ] **Step 3: Build to verify**

Run: `cd docs && npm run build`
Expected: Clean compilation, no errors.

- [ ] **Step 4: Commit**

```bash
git add docs/src/index.ts docs/src/tools/search-docs.ts
git commit -m "fix(docs): proper error handling in main() and search initialization"
```

---

### Task 6: Final build and smoke test

**Files:**
- No modifications — verification only.

- [ ] **Step 1: Clean build**

Run: `cd docs && rm -rf dist && npm run build`
Expected: Clean compilation, all files in `dist/`.

- [ ] **Step 2: Smoke test — server starts without error**

Run: `cd docs && echo '{"jsonrpc":"2.0","method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"test","version":"0.1"}},"id":1}' | node dist/index.js 2>stderr.log; cat stderr.log; rm stderr.log`

Expected: Server outputs JSON-RPC response to stdout, stderr shows `[arenula-docs] Server running on stdio` and seed indexing logs.

- [ ] **Step 3: Commit plan doc and close out**

```bash
git add docs/superpowers/plans/2026-04-05-docs-hardening.md
git commit -m "docs: hardening plan for arenula-docs"
```
