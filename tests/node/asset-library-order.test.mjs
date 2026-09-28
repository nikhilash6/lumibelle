import test from 'node:test';
import assert from 'node:assert/strict';
import { attach } from '../../src/Lumibelle.UI/wwwroot/asset-library-order.js';

test('leaving Assets before drag initialization finishes permits safe cleanup', () => {
  for (const list of [null, { isConnected: false }]) {
    const drag = attach(list, {});
    assert.doesNotThrow(() => drag.dispose());
  }
});
