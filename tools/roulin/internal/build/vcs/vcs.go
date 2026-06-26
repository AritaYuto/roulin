package vcs

type VCSAdapter interface {
	CurrentRevision() (string, error)

	// since="" means "no base" — caller treats it as a full-rebuild signal.
	ChangedFiles(since string) ([]string, error)

	UncommittedFiles() ([]string, error)
}
