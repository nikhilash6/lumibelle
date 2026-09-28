import test from 'node:test';
import assert from 'node:assert/strict';
import { atTime, duration, locate, preservePosition, timeOf, trimEdge } from '../../src/Lumibelle.UI/wwwroot/cut-time.js';
const a = { id:'a', takeId:'one', startFrame:10, endFrameExclusive:34, frameCount:56, fps:24 };
const b = { ...a, id:'b', startFrame:0, endFrameExclusive:1 };
const c = { ...a, id:'c', takeId:'two', fps:30 };
test('inclusive source frames map across exclusive boundaries and repeated takes', () => {
    assert.equal(duration(a),1);
    assert.deepEqual(atTime([a,b],0),{clipId:'a',takeId:'one',frame:10});
    assert.deepEqual(atTime([a,b],1),{clipId:'b',takeId:'one',frame:0});
    assert.deepEqual(atTime([a,b],999),{clipId:'b',takeId:'one',frame:0});
    assert.deepEqual(locate([a,b],1),{index:1,offset:0});
    assert.equal(timeOf([a,c],{clipId:'c',frame:25}),1.5);
});
test('trims extend to originals and cannot remove the last retained frame', () => {
    assert.equal(trimEdge(a,'start',-50).startFrame,0);
    assert.equal(trimEdge(a,'end',999).endFrameExclusive,56);
    assert.equal(trimEdge(a,'start',999).startFrame,33);
    assert.equal(trimEdge(a,'end',-999).endFrameExclusive,11);
    assert.deepEqual(trimEdge(a,'start',10.6),{...a,startFrame:11});
});
test('position follows source frame through reorder and trim, replacement resets it', () => {
    const p={clipId:'a',takeId:'one',frame:20};
    assert.deepEqual(preservePosition([a,b],[b,a],p),p);
    assert.deepEqual(preservePosition([a,b],[{...a,startFrame:25},b],p),{...p,frame:25});
    assert.deepEqual(preservePosition([a],[{...a,takeId:'new',startFrame:0}],p),{...p,takeId:'new',frame:0});
});
test('removal selects following start, then preceding end, then handles an empty cut', () => {
    const p={clipId:'b',takeId:'one',frame:0};
    assert.deepEqual(preservePosition([a,b,c],[a,c],p),{clipId:'c',takeId:'two',frame:10});
    assert.deepEqual(preservePosition([a,b],[a],p),{clipId:'a',takeId:'one',frame:33});
    assert.equal(preservePosition([a,b],[],p),null);
});
