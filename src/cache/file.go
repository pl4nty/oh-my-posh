package cache

import (
	"encoding/json"
	"os"
	"sync"
	"time"

	"github.com/jandedobbeleer/oh-my-posh/src/log"
	"github.com/jandedobbeleer/oh-my-posh/src/maps"
)

type File struct {
	cache         *maps.Concurrent[any]
	cacheFilePath string
	dirty         bool
	persist       bool

	// shared files stay loaded between prompts and are written in the background
	shared  bool
	mu      sync.Mutex
	pending []byte
	writing bool
}

var (
	sharedFiles   = map[string]*File{}
	sharedFilesMu sync.Mutex
)

// Shared returns the cache file for cacheFilePath, loading it from disk only the first time.
// It's for a process that renders many prompts, where reloading and rewriting the
// file on every prompt would dominate the render time.
func Shared(cacheFilePath string, persist bool) *File {
	sharedFilesMu.Lock()
	defer sharedFilesMu.Unlock()

	fc, ok := sharedFiles[cacheFilePath]
	if !ok {
		fc = &File{shared: true}
		fc.Init(cacheFilePath, persist)
		sharedFiles[cacheFilePath] = fc
		return fc
	}

	fc.persist = persist

	// Init drops expired keys on load; a file that stays loaded has to do it here
	for key, val := range fc.cache.ToSimple() {
		if co, ok := val.(*Entry); ok && co.Expired() {
			fc.cache.Delete(key)
			fc.dirty = true
		}
	}

	return fc
}

func (fc *File) Init(cacheFilePath string, persist bool) {
	defer log.Trace(time.Now(), cacheFilePath)

	fc.cache = maps.NewConcurrent[any]()
	fc.cacheFilePath = cacheFilePath
	fc.persist = persist

	log.Debug("loading cache file:", fc.cacheFilePath)

	content, err := os.ReadFile(fc.cacheFilePath)
	if err != nil {
		// set to dirty so we create it on close
		fc.dirty = true
		log.Error(err)
		return
	}

	var list map[string]*Entry
	if err := json.Unmarshal(content, &list); err != nil {
		return
	}

	for key, co := range list {
		if co.Expired() {
			log.Debug("skipping expired cache key:", key)
			continue
		}

		log.Debug("loading cache key:", key)
		fc.cache.Set(key, co)
	}
}

func (fc *File) Close() {
	if fc == nil || !fc.persist || !fc.dirty {
		log.Debug("not persisting cache")
		return
	}

	cache := fc.cache.ToSimple()

	dump, err := json.MarshalIndent(cache, "", "    ")
	if err != nil {
		return
	}

	if !fc.shared {
		_ = os.WriteFile(fc.cacheFilePath, dump, 0o644)
		return
	}

	// snapshot now, write later; only the latest snapshot gets written
	fc.dirty = false
	fc.mu.Lock()
	fc.pending = dump
	start := !fc.writing
	fc.writing = true
	fc.mu.Unlock()

	if start {
		go fc.flush()
	}
}

func (fc *File) flush() {
	for {
		fc.mu.Lock()
		dump := fc.pending
		fc.pending = nil
		if dump == nil {
			fc.writing = false
			fc.mu.Unlock()
			return
		}
		fc.mu.Unlock()

		_ = os.WriteFile(fc.cacheFilePath, dump, 0o644)
	}
}

// returns the value for the given key as long as
// the duration is not expired
func (fc *File) Get(key string) (string, bool) {
	if fc == nil {
		log.Debug("cache is nil, returning empty value for key:", key)
		return "", false
	}

	val, found := fc.cache.Get(key)
	if !found {
		log.Debug("cache key not found:", key)
		return "", false
	}

	if co, ok := val.(*Entry); ok {
		log.Debug("getting cache key:", key, "with value:", co.Value)
		return co.Value, true
	}

	log.Debug("unable to parse cache key:", key)
	return "", false
}

// sets the value for the given key with a duration
func (fc *File) Set(key, value string, duration Duration) {
	if fc == nil {
		log.Debug("cache is nil, not setting value for key:", key)
		return
	}

	seconds := duration.Seconds()

	if seconds == 0 {
		return
	}

	log.Debug("setting cache key:", key, "with duration:", string(duration))

	fc.cache.Set(key, &Entry{
		Value:     value,
		Timestamp: time.Now().Unix(),
		TTL:       seconds,
	})

	fc.dirty = true
}

// delete the key from the cache
func (fc *File) Delete(key string) {
	if fc == nil {
		log.Debug("cache is nil, not deleting key:", key)
		return
	}

	log.Debug("deleting cache key:", key)
	fc.cache.Delete(key)
	fc.dirty = true
}
