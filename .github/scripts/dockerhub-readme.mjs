#!/usr/bin/env node
// Turns README.md into a Docker Hub description (printed to stdout):
// - --sections "Run it,Alerts" keeps the README's introduction (everything before the first "## ") and only those
//   "## " sections (with their subsections), in the order given, then lists the other sections as links to GitHub;
//   without it the whole README is used;
// - relative links point to GitHub (files and folders to github.com, images to raw.githubusercontent.com);
// - "#section" links point to that section of the README on GitHub (Docker Hub has no heading anchors);
// - GitHub-only alerts ("> [!NOTE]") become plain bold labels;
// - a link to the GitHub repository goes at <!-- dockerhub:github-link --> (or at the top);
// - code blocks are left alone; the result is cut to Docker Hub's 25,000-byte limit with a link to the rest.
//
// Usage: node dockerhub-readme.mjs README.md [owner/repo] [branch] [--sections "A,B"] [--title "…"]
// owner/repo and branch default to GITHUB_REPOSITORY and GITHUB_REF_NAME (then the origin remote and main);
// --title replaces the README's first "# " heading (e.g. with the image's name).
// Kept in step with the same script in the owner's other repositories (abook, nopds, …); --sections and --title are additions.
import { execSync } from 'node:child_process'
import { readFileSync } from 'node:fs'
import { posix } from 'node:path'

const args = process.argv.slice(2)
const option = (name) => {
  const i = args.indexOf(`--${name}`)
  if (i < 0) return undefined
  const value = args[i + 1]
  args.splice(i, 2)
  return value
}
const sections = option('sections')?.split(',').map((s) => s.trim()).filter(Boolean)
const title = option('title')
const [file = 'README.md', repoArg, refArg] = args
const repo = repoArg || process.env.GITHUB_REPOSITORY || originRepo()
const ref = refArg || (process.env.GITHUB_REF_TYPE === 'branch' && process.env.GITHUB_REF_NAME) || 'main'
const home = `https://github.com/${repo}`
const dir = posix.dirname(file.replaceAll('\\', '/'))
const LIMIT = 25000

function resolve(target, image) {
  if (/^[a-z][a-z0-9+.-]*:/i.test(target) || target.startsWith('//')) return target
  if (target.startsWith('#')) return `${home}${target}`
  const [path, hash = ''] = target.split(/(?=#)/)
  const clean = posix.normalize(posix.join(dir === '.' ? '' : dir, path)).replace(/^(\.\/)+/, '')
  return image ? `https://raw.githubusercontent.com/${repo}/${ref}/${clean}` : `${home}/blob/${ref}/${clean}${hash}`
}

function convertLine(line) {
  return (
    line
      // ![alt](target "title") and [text](target "title")
      .replace(/(!?)\[([^\]]*)\]\(\s*<?([^)\s>]+)>?(\s+"[^"]*")?\s*\)/g, (_, bang, text, target, t = '') =>
        `${bang}[${text}](${resolve(target, bang === '!')}${t})`)
      // the outer link of a badge, [![alt](image)](target), whose text holds brackets
      .replace(/(\)\]\(\s*)<?([^)\s>]+)>?(\s+"[^"]*")?(\s*\))/g, (_, head, target, t = '', tail) =>
        `${head}${resolve(target, false)}${t}${tail}`)
      // [label]: target (reference definitions)
      .replace(/^(\s{0,3}\[[^\]]+\]:\s*)(\S+)/, (_, head, target) => head + resolve(target, false))
      // <img src="…">, <a href="…">
      .replace(/\b(src|href)="([^"]+)"/g, (_, attr, target) => `${attr}="${resolve(target, attr === 'src')}"`)
      // GitHub alerts
      .replace(/^(\s*>\s*)\[!(NOTE|TIP|IMPORTANT|WARNING|CAUTION)\]\s*$/, (_, q, kind) => `${q}**${kind[0]}${kind.slice(1).toLowerCase()}**`)
  )
}

// GitHub's heading anchor: lower case, punctuation dropped, spaces to dashes.
const anchor = (heading) => heading.trim().toLowerCase().replace(/[^\p{L}\p{N}\s_-]/gu, '').replace(/\s/g, '-')

// Splits the README into its introduction and "## " sections, ignoring headings inside code blocks.
function split(lines) {
  const parts = [{ heading: null, lines: [] }]
  let fence = null
  for (const line of lines) {
    const m = line.match(/^\s*(`{3,}|~{3,})/)
    if (m) {
      if (!fence) fence = m[1]
      else if (m[1][0] === fence[0] && m[1].length >= fence.length) fence = null
    }
    const h = !fence && line.match(/^## (.+?)\s*$/)
    if (h) parts.push({ heading: h[1], lines: [] })
    parts.at(-1).lines.push(line)
  }
  return parts
}

let lines = readFileSync(file, 'utf8').split('\n')
if (title) {
  const i = lines.findIndex((l) => /^# /.test(l))
  if (i >= 0) lines[i] = `# ${title}`
}
if (sections) {
  const parts = split(lines)
  const known = new Set(parts.map((p) => p.heading))
  const missing = sections.filter((s) => !known.has(s))
  if (missing.length) {
    console.error(`dockerhub-readme: no "## " section named ${missing.map((s) => `"${s}"`).join(', ')} in ${file}`)
    process.exit(2)
  }
  const kept = [parts[0], ...sections.map((s) => parts.find((p) => p.heading === s))]
  const others = parts.filter((p) => p.heading !== null && !sections.includes(p.heading))
  lines = kept.flatMap((p) => p.lines)
  if (others.length) {
    while (lines.length && lines.at(-1).trim() === '') lines.pop()
    lines.push('', '## More in the README on GitHub', '',
      ...others.map((p) => `- [${p.heading}](${home}#${anchor(p.heading)})`), '')
  }
}

let fence = null
const out = lines
  .map((line) => {
    const m = line.match(/^\s*(`{3,}|~{3,})/)
    if (m) {
      if (!fence) fence = m[1]
      else if (m[1][0] === fence[0] && m[1].length >= fence.length) fence = null
      return line
    }
    return fence ? line : convertLine(line)
  })
  .join('\n')

const banner = `> **Source code, issues and release notes:** [github.com/${repo}](${home})`
let text = out.includes('<!-- dockerhub:github-link -->')
  ? out.replace('<!-- dockerhub:github-link -->', banner)
  : out.replace(/^(# .*\n)/, `$1\n${banner}\n`) // under the title when there is one
if (!text.includes(banner)) text = `${banner}\n\n${text}`

if (Buffer.byteLength(text) > LIMIT) {
  const tail = `\n\n---\n\n*The rest of this page is on [GitHub](${home}#readme).*\n`
  let cut = text.slice(0, LIMIT - Buffer.byteLength(tail) - 4)
  while (Buffer.byteLength(cut) > LIMIT - Buffer.byteLength(tail)) cut = cut.slice(0, -100)
  text = cut.slice(0, cut.lastIndexOf('\n\n')) + tail
}

process.stdout.write(text)

function originRepo() {
  const url = execSync('git remote get-url origin', { encoding: 'utf8' }).trim()
  return url.replace(/^.*github\.com[:/]/, '').replace(/\.git$/, '')
}
