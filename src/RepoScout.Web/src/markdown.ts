import { marked } from 'marked'
import DOMPurify from 'dompurify'

DOMPurify.addHook('afterSanitizeAttributes', (node) => {
  if (node.tagName === 'A') {
    node.setAttribute('target', '_blank')
    node.setAttribute('rel', 'noopener noreferrer')
  }
})

// Only producer of v-html input: Markdown -> HTML -> sanitized HTML.
export function renderMarkdown(md: string): string {
  const html = marked.parse(md, { async: false })
  return DOMPurify.sanitize(html, { FORBID_TAGS: ['img'] })
}
