import {readFile} from 'node:fs/promises';
import {build} from 'esbuild';
for(const name of ['script-editor','prompt-editor']) {
 const destination=`src/Lumibelle.UI/wwwroot/${name}.js`;
 const result=await build({entryPoints:[`src/Lumibelle.UI/Client/${name}.js`],bundle:true,format:'esm',minify:true,outfile:destination,legalComments:'linked',write:false});
 for(const output of result.outputFiles) {
  const saved=await readFile(output.path);
  if(!saved.equals(Buffer.from(output.contents))) throw new Error(`${output.path} is stale. Run npm run build and commit the generated bundles.`);
 }
}
console.log('Checked-in editor bundles match their shared sources.');
