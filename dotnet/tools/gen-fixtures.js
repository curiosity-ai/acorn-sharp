// Fixture generator: captures every test()/testFail() case defined in the
// original acorn JS test suite and serializes it to JSON so the C# port can
// run the exact same conformance corpus.
//
// Strategy:
//  * Load ./test/driver.js and replace its test/testFail/testAssert exports
//    with capturing versions BEFORE loading the test files (module cache is
//    shared, so the test files pick up our versions via `var test = driver.test`).
//  * Stub `require("../acorn")` and `require("../acorn-loose")` via a Module._load
//    hook, since the dist build does not exist and the test data barely uses it.
//  * Serialize with a replacer that encodes RegExp and BigInt with markers the
//    C# comparator understands.

const path = require("path")
const fs = require("fs")
const Module = require("module")

const ROOT = path.resolve(__dirname, "../..")
const TEST_DIR = path.join(ROOT, "test")
const OUT_DIR = path.resolve(__dirname, "../fixtures")

// --- Stub the acorn requires that the test files do at their top level ---
const origLoad = Module._load
const acornStub = new Proxy({}, {
  get() { return new Proxy({}, {get() { return {} }}) }
})
Module._load = function(request, parent, isMain) {
  if (request === "../acorn" || request === "../acorn-loose" ||
      request === "../acorn/dist/acorn.js" || request.endsWith("/acorn") ||
      request.endsWith("/acorn-loose")) {
    return acornStub
  }
  return origLoad.apply(this, arguments)
}

// --- Replace driver capture hooks ---
const driver = require(path.join(TEST_DIR, "driver.js"))
const cases = []
let group = "unknown"
driver.test = function(code, ast, options) {
  cases.push({group, code, ast, options: options || null})
}
driver.testFail = function(code, error, options) {
  cases.push({group, code, error, options: options || null})
}
driver.testAssert = function() { /* function-based assertions cannot be serialized */ }

// --- The list of test files (mirrors test/run.js order) ---
const files = [
  "tests.js", "tests-harmony.js", "tests-es7.js", "tests-asyncawait.js",
  "tests-await-top-level.js", "tests-trailing-commas-in-func.js",
  "tests-template-literal-revision.js", "tests-directive.js",
  "tests-rest-spread-properties.js", "tests-async-iteration.js",
  "tests-regexp.js", "tests-regexp-2018.js", "tests-regexp-2020.js",
  "tests-regexp-2022.js", "tests-regexp-2024.js", "tests-regexp-2025.js",
  "tests-json-superset.js", "tests-optional-catch-binding.js",
  "tests-bigint.js", "tests-dynamic-import.js", "tests-export-named.js",
  "tests-export-all-as-ns-from-source.js", "tests-import-meta.js",
  "tests-nullish-coalescing.js", "tests-optional-chaining.js",
  "tests-logical-assignment-operators.js", "tests-numeric-separators.js",
  "tests-class-features-2022.js", "tests-module-string-names.js",
  "tests-import-attributes.js", "tests-using.js", "tests-commonjs.js"
]

for (const f of files) {
  group = f
  const before = cases.length
  try {
    require(path.join(TEST_DIR, f))
  } catch (e) {
    console.error(`FAILED loading ${f}: ${e.stack}`)
  }
  console.error(`${f}: ${cases.length - before} cases`)
}

// --- Serialization replacer ---
// Detect strings containing lone UTF-16 surrogates (which System.Text.Json
// refuses to decode). Such strings are encoded as {$str16:[charCodes]} so the
// C# side can rebuild them from code units.
function hasLoneSurrogate(s) {
  for (let i = 0; i < s.length; i++) {
    const c = s.charCodeAt(i)
    if (c >= 0xD800 && c <= 0xDBFF) {
      const n = s.charCodeAt(i + 1)
      if (!(n >= 0xDC00 && n <= 0xDFFF)) return true
      i++
    } else if (c >= 0xDC00 && c <= 0xDFFF) {
      return true
    }
  }
  return false
}
function encStr(s) {
  if (typeof s !== "string") return s
  if (!hasLoneSurrogate(s)) return s
  const codes = new Array(s.length)
  for (let i = 0; i < s.length; i++) codes[i] = s.charCodeAt(i)
  return {$str16: codes}
}
function deepEncode(v, seen) {
  if (v === null || v === undefined) return v
  if (typeof v === "bigint") return {$bigint: v.toString()}
  if (v instanceof RegExp) return {$regexp: v.toString()}
  if (typeof v === "function") return undefined
  if (typeof v === "string") return encStr(v)
  if (typeof v === "number" && !isFinite(v))
    return {$number: v === Infinity ? "Infinity" : (v === -Infinity ? "-Infinity" : "NaN")}
  if (Array.isArray(v)) return v.map(x => deepEncode(x, seen))
  if (typeof v === "object") {
    const out = {}
    for (const k in v) {
      const ev = deepEncode(v[k], seen)
      if (ev !== undefined) out[k] = ev
    }
    return out
  }
  return v
}

const encoded = cases.map(c => ({
  group: c.group,
  code: encStr(c.code),
  ast: c.ast !== undefined ? deepEncode(c.ast, null) : undefined,
  error: encStr(c.error),
  options: c.options ? deepEncode(c.options, null) : null,
  isFail: c.error !== undefined
}))

if (!fs.existsSync(OUT_DIR)) fs.mkdirSync(OUT_DIR, {recursive: true})

// Write one combined file and per-group files.
fs.writeFileSync(path.join(OUT_DIR, "all.json"), JSON.stringify(encoded))
console.error(`\nTOTAL: ${encoded.length} cases written to ${OUT_DIR}/all.json`)
console.error(`Size: ${(fs.statSync(path.join(OUT_DIR, "all.json")).size / 1024 / 1024).toFixed(1)} MB`)
