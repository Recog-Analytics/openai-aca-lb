import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// The dashboard service serves the build from its wwwroot. The dev server proxies the stream to it.
export default defineConfig({
  plugins: [react()],
  build: { outDir: "../server/wwwroot", emptyOutDir: true, chunkSizeWarningLimit: 1200 },
  server: { proxy: { "/api": { target: process.env.DASHBOARD_URL ?? "http://localhost:5200", changeOrigin: true } } },
});
