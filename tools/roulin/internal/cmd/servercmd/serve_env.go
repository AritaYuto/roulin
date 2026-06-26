package servercmd

type serveEnv struct {
	LogDir           string `env:"ROULIN_LOG_DIR" envDefault:"./logs"`
	Storage          string `env:"ROULIN_STORAGE"`
	StorageEndpoint  string `env:"ROULIN_STORAGE_ENDPOINT"`
	StoragePathStyle bool   `env:"ROULIN_STORAGE_PATH_STYLE"`
	StorageRegion    string `env:"ROULIN_STORAGE_REGION"`
	CacheMemoryBytes int64  `env:"ROULIN_CACHE_MEMORY"`
	CacheDir         string `env:"ROULIN_CACHE_DIR"`
	Port             int    `env:"ROULIN_PORT" envDefault:"8765"`
	VCSProjectRoot   string `env:"ROULIN_VCS_PROJECT_ROOT" envDefault:"/repo"`
	VCSPathspecs     string `env:"ROULIN_VCS_PATHSPECS"`
}
