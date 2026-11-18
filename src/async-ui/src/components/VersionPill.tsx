const REPOSITORY_URL = 'https://github.com/jncchds/ASync'

/** The app's version; opens the project's GitHub repository in a new tab. */
export default function VersionPill() {
  return (
    <a
      className="version-pill"
      href={REPOSITORY_URL}
      target="_blank"
      rel="noopener noreferrer"
      title="ASync on GitHub"
    >
      v{__APP_VERSION__}
    </a>
  )
}
