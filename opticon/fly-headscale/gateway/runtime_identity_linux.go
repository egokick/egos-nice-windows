//go:build linux

package main

import "syscall"

func setRuntimeIdentity(uid, gid int) error {
	if err := syscall.Setgroups([]int{gid}); err != nil {
		return err
	}
	if err := syscall.Setgid(gid); err != nil {
		return err
	}
	return syscall.Setuid(uid)
}
