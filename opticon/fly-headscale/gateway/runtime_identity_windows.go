//go:build windows

package main

import "errors"

func setRuntimeIdentity(_, _ int) error {
	return errors.New("the Opticon gateway runtime is supported only on Linux")
}
