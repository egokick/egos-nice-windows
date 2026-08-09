//go:build windows

package main

// The gateway is deployed only on Linux. Windows does not support fsync on a
// directory handle; this no-op exists so the security tests can exercise the
// O_EXCL nonce algorithm on Windows without weakening the Linux implementation.
func syncDirectory(string) error { return nil }
