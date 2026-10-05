import { defineConfig } from 'vite';
import plugin from '@vitejs/plugin-vue';
import fs from 'node:fs';
import path from 'node:path';

let outDir = 'dist';

// https://vitejs.dev/config/
export default defineConfig({
    base: '/',
    plugins: [plugin(), {
        name: 'generate-404-fallback',
        configResolved(config) {
            outDir = config.build.outDir;
        },
        // Vite 8（rolldown）下 closeBundle 可能早于 HTML 产物落盘，改用 writeBundle
        writeBundle() {
            // Cloudflare Pages 对未命中静态文件的路径（如 /guide/x 深层链接）会返回 404.html，
            // 因此把 index.html 复制为 404.html 作为 SPA 回退，前端路由照常渲染
            const indexFile = path.join(outDir, 'index.html');
            if (fs.existsSync(indexFile)) {
                fs.copyFileSync(indexFile, path.join(outDir, '404.html'));
            } else {
                this.warn('generate-404-fallback: dist/index.html 不存在，跳过 404 回退');
            }
        }
    }],
    server: {
        port: 63179,
    }
})
