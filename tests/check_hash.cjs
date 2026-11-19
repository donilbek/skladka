// Compare the browser's incremental implementation with the system SHA-256.
const assert = require('node:assert/strict');
const {createHash, randomBytes} = require('node:crypto');
const {readFileSync} = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const source = process.argv[2] || path.join(__dirname,'../web/sha256.js');
const Sha256 = vm.runInNewContext(readFileSync(source,'utf8') + '\nSha256');
for (const n of [0,1,55,56,63,64,65,119,120,127,128,129,524289]) {
    const bytes = randomBytes(n);
    const expected = createHash('sha256').update(bytes).digest('hex');
    for (const chunk of [7,64,65536]) {
        const actual = new Sha256();
        for (let i=0; i<n; i+=chunk) actual.update(bytes.subarray(i,i+chunk));
        assert.equal(Buffer.from(actual.finish()).toString('hex'),expected);
    }
}
console.log('Streaming SHA-256 passes reference, alignment and padding checks.');
