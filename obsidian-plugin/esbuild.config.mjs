import esbuild from 'esbuild';

esbuild.build({
  entryPoints: ['src/main.ts'],
  bundle: true,
  outfile: 'main.js',
  external: ['obsidian'],
  format: 'cjs',
  target: 'es2020',
  platform: 'node',
  sourcemap: true,
  minify: process.argv.includes('--minify')
}).catch(() => process.exit(1));
