package vcs

import (
	"fmt"
	"os/exec"
	"strings"
)

type GitAdapter struct {
	WorkDir   string
	Pathspecs []string
}

func (g *GitAdapter) withPathspecs(args []string) []string {
	if len(g.Pathspecs) == 0 {
		return args
	}
	out := make([]string, 0, len(args)+1+len(g.Pathspecs))
	out = append(out, args...)
	out = append(out, "--")
	out = append(out, g.Pathspecs...)
	return out
}

func (g *GitAdapter) run(args ...string) (string, error) {
	cmd := exec.Command("git", args...)
	if g.WorkDir != "" {
		cmd.Dir = g.WorkDir
	}
	out, err := cmd.Output()
	if err != nil {
		return "", fmt.Errorf("git %s: %w", strings.Join(args, " "), err)
	}
	return string(out), nil
}

func (g *GitAdapter) CurrentRevision() (string, error) {
	out, err := g.run("rev-parse", "HEAD")
	if err != nil {
		return "", err
	}
	return strings.TrimSpace(out), nil
}

func (g *GitAdapter) ChangedFiles(since string) ([]string, error) {
	if since == "" {
		return nil, nil
	}
	args := g.withPathspecs([]string{"diff", "--name-only", since, "HEAD"})
	out, err := g.run(args...)
	if err != nil {
		return nil, err
	}
	return splitLines(out), nil
}

func (g *GitAdapter) UncommittedFiles() ([]string, error) {
	args := g.withPathspecs([]string{
		"-c", "status.renames=false",
		"status", "--porcelain", "-z", "--untracked-files=all",
	})
	out, err := g.run(args...)
	if err != nil {
		return nil, err
	}
	return parseStatusPorcelainZ(out), nil
}

func parseStatusPorcelainZ(s string) []string {
	if s == "" {
		return nil
	}
	var paths []string
	for rec := range strings.SplitSeq(s, "\x00") {
		if len(rec) < 4 {
			continue
		}
		paths = append(paths, rec[3:])
	}
	return paths
}

func splitLines(s string) []string {
	s = strings.TrimRight(s, "\n")
	if s == "" {
		return nil
	}
	return strings.Split(s, "\n")
}
