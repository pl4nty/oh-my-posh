// Package main builds oh-my-posh as a C shared library, so a long-lived shell
// can render prompts in-process instead of launching oh-my-posh.exe each time.
//
//	CGO_ENABLED=1 GOOS=windows GOARCH=amd64 CC=x86_64-w64-mingw32-gcc \
//	  go build -buildmode=c-shared -trimpath -ldflags="-s -w" -o oh-my-posh.dll ./cdll
package main

/*
#include <stdlib.h>
*/
import "C"

import (
	"fmt"
	"strings"
	"sync"
	"unsafe"

	"github.com/jandedobbeleer/oh-my-posh/src/prompt"
	"github.com/jandedobbeleer/oh-my-posh/src/runtime"
	"github.com/jandedobbeleer/oh-my-posh/src/template"

	"github.com/spf13/pflag"
)

// errPrefix marks an error result instead of a rendered prompt.
const errPrefix = "OMPERR:"

var mu sync.Mutex

func init() {
	runtime.InProcess = true
}

// OmpPrint takes the arguments of `oh-my-posh print ...`, separated by \x1f,
// and returns what that command would write to stdout. Free the result with OmpFree.
//
//export OmpPrint
func OmpPrint(args *C.char) *C.char {
	mu.Lock()
	defer mu.Unlock()

	out, err := render(strings.Split(C.GoString(args), "\x1f"))
	if err != nil {
		out = errPrefix + err.Error()
	}

	return C.CString(out)
}

//export OmpFree
func OmpFree(p *C.char) {
	C.free(unsafe.Pointer(p))
}

// render mirrors cli/print.go, with a fresh flag set per call so no flag leaks into the next prompt.
func render(argv []string) (out string, err error) {
	defer func() {
		if r := recover(); r != nil {
			err = fmt.Errorf("panic: %v", r)
		}
	}()

	var flags runtime.Flags
	var command string

	fs := pflag.NewFlagSet("print", pflag.ContinueOnError)
	fs.StringVarP(&flags.Config, "config", "c", "", "")
	fs.StringVar(&flags.PWD, "pwd", "", "")
	fs.StringVar(&flags.PSWD, "pswd", "", "")
	fs.StringVar(&flags.Shell, "shell", "", "")
	fs.StringVar(&flags.ShellVersion, "shell-version", "", "")
	fs.IntVar(&flags.ErrorCode, "status", 0, "")
	fs.BoolVar(&flags.NoExitCode, "no-status", false, "")
	fs.StringVar(&flags.PipeStatus, "pipestatus", "", "")
	fs.Float64Var(&flags.ExecutionTime, "execution-time", 0, "")
	fs.IntVarP(&flags.StackCount, "stack-count", "s", 0, "")
	fs.IntVarP(&flags.TerminalWidth, "terminal-width", "w", 0, "")
	fs.StringVar(&command, "command", "", "")
	fs.BoolVarP(&flags.Plain, "plain", "p", false, "")
	fs.BoolVar(&flags.Cleared, "cleared", false, "")
	fs.BoolVar(&flags.Eval, "eval", false, "")
	fs.IntVar(&flags.Column, "column", 0, "")
	fs.IntVar(&flags.JobCount, "job-count", 0, "")
	fs.BoolVar(&flags.SaveCache, "save-cache", false, "")
	fs.BoolVar(&flags.Escape, "escape", true, "")
	fs.BoolVarP(&flags.Force, "force", "f", false, "")

	if err := fs.Parse(argv); err != nil {
		return "", err
	}

	pos := fs.Args()
	if len(pos) != 2 || pos[0] != "print" {
		return "", fmt.Errorf("unsupported arguments: %q", pos)
	}

	flags.Type = pos[1]
	flags.IsPrimary = flags.Type == prompt.PRIMARY

	eng := prompt.New(&flags)
	defer func() {
		template.SaveCache()
		eng.Env.Close()
	}()

	switch flags.Type {
	case prompt.DEBUG:
		return eng.ExtraPrompt(prompt.Debug), nil
	case prompt.PRIMARY:
		return eng.Primary(), nil
	case prompt.SECONDARY:
		return eng.ExtraPrompt(prompt.Secondary), nil
	case prompt.TRANSIENT:
		return eng.ExtraPrompt(prompt.Transient), nil
	case prompt.RIGHT:
		return eng.RPrompt(), nil
	case prompt.TOOLTIP:
		return eng.Tooltip(command), nil
	case prompt.VALID:
		return eng.ExtraPrompt(prompt.Valid), nil
	case prompt.ERROR:
		return eng.ExtraPrompt(prompt.Error), nil
	case prompt.PREVIEW:
		return eng.Preview(), nil
	}

	return "", fmt.Errorf("unsupported prompt type: %s", flags.Type)
}

func main() {}
