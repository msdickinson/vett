package sandbox

import "os"

func osEnvLookup(key string) string { return os.Getenv(key) }

func osReadDirFn(dir string) ([]os.DirEntry, error) { return os.ReadDir(dir) }
