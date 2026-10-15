const fs = require('fs');
const crypto = require('crypto');
const lz = require(process.env.LZUTF8_MODULE || require('path').resolve(__dirname, '../../KeepmarkApp/node_modules/lzutf8'));
const data = JSON.parse(fs.readFileSync(process.argv[2] || '/tmp/keepmark-csharp-blobs.json','utf8'));
for (const v of data.cases) {
  const all=Buffer.from(v.blob,'base64');
  const dec=crypto.createDecipheriv('aes-256-gcm',Buffer.from(data.key,'base64'),all.subarray(0,16));
  dec.setAuthTag(all.subarray(-16));
  const compressed=Buffer.concat([dec.update(all.subarray(16,-16)),dec.final()]);
  const actual=lz.decompress(new Uint8Array(compressed),{inputEncoding:'ByteArray',outputEncoding:'String'});
  if(actual!==v.plain) throw new Error(`Mismatch ${v.name}`);
  console.log(`C# -> Node lzutf8@0.6.0: ${v.name} OK`);
}

// lzutf8's Node scheduler may retain a MessagePort; all verification above is synchronous.
process.exit(0);
