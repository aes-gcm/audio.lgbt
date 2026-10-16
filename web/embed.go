package web

import "embed"

// FS is the embedded web client (HTML, JS, CSS and the self-hosted font).
//
//go:embed index.html download.html download.js app.js recent-rooms.js server-auth.js style.css e2ee-worker.js fonts downloads
var FS embed.FS
