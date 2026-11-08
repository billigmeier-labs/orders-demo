import { expect, request, test } from '@playwright/test';
import { settings } from '../support/settings';

// Fresh request contexts: they do not carry the suite's X-Verification-Token header.
const anonymous = () => request.newContext({ baseURL: settings.baseURL, extraHTTPHeaders: {} });
const withToken = (token: string) =>
  request.newContext({ baseURL: settings.baseURL, extraHTTPHeaders: { 'X-Verification-Token': token } });

test.describe('sign-in', () => {
  test.skip(!settings.token, 'CONSOLE_VERIFICATION_TOKEN is not set, so the console under test may have auth off.');

  test('/health/live needs no sign-in', async () => {
    const ctx = await anonymous();
    expect((await ctx.get('/health/live')).status()).toBe(200);
    await ctx.dispose();
  });

  test('no token: pages redirect to Google sign-in or 401, the API is 401', async () => {
    const ctx = await anonymous();
    const page = await ctx.get('/', { maxRedirects: 0 });
    if (page.status() === 302) {
      expect(page.headers()['location']).toMatch(/^https:\/\/accounts\.google\.com\//);
    } else {
      expect(page.status()).toBe(401);
    }
    expect((await ctx.get('/api/whoami', { maxRedirects: 0 })).status()).toBe(401);
    expect((await ctx.post('/api/load/stop', { headers: { 'X-Console-Action': '1' }, maxRedirects: 0 })).status()).toBe(401);
    await ctx.dispose();
  });

  test('wrong token: 401', async () => {
    const ctx = await withToken(`wrong-${settings.token}`);
    for (const path of ['/', '/present', '/api/whoami']) {
      expect((await ctx.get(path, { maxRedirects: 0 })).status(), path).toBe(401);
    }
    await ctx.dispose();
  });

  test('right token: 200, signed in as automation', async () => {
    const ctx = await withToken(settings.token!);
    for (const path of ['/', '/present', '/architecture']) {
      expect((await ctx.get(path, { maxRedirects: 0 })).status(), path).toBe(200);
    }
    const whoami = await ctx.get('/api/whoami');
    expect(whoami.status()).toBe(200);
    expect(await whoami.json()).toMatchObject({ authenticated: true, name: 'automation', method: 'verification-token' });
    await ctx.dispose();
  });
});
