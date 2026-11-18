const AUTHOR_URL = 'https://github.com/jncchds'
const CLAUDE_URL = 'https://claude.com/claude-code'
const REPOSITORY_URL = 'https://github.com/jncchds/ASync'

function External({ href, children }: { href: string; children: string }) {
  return <a href={href} target="_blank" rel="noopener noreferrer">{children}</a>
}

/** The one-line credit under the sidebar and the sign-in card. */
export function CreditsLine({ className }: { className?: string }) {
  return (
    <div className={className}>
      by <External href={AUTHOR_URL}>CHDS</External> · built with <External href={CLAUDE_URL}>Claude</External>
    </div>
  )
}

/** The full credits, on the Account page. */
export function CreditsCard() {
  return (
    <section className="card section" aria-label="Credits">
      <h2>Credits</h2>
      <p>
        <strong>ASync by CHDS</strong>: created and maintained by <External href={AUTHOR_URL}>CHDS</External>, built together
        with <External href={CLAUDE_URL}>Claude</External> by Anthropic.
      </p>
      <p className="muted">
        Version {__APP_VERSION__}. Source code, releases and issues on <External href={REPOSITORY_URL}>GitHub</External>.
      </p>
    </section>
  )
}
