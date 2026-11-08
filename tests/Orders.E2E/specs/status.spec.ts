import { expect, test } from '@playwright/test';
import { settings } from '../support/settings';

test('Status renders health, alarms, GitHub activity, flags and cost', async ({ page }) => {
  await page.goto('/');
  await expect(page.getByTestId('nav-status')).toHaveAttribute('aria-current', 'page');
  await expect(page.getByTestId('identity')).toHaveText('automation');
  await expect(page.locator('body')).toHaveAttribute('data-fakes', String(!settings.live));
  await expect(page.getByTestId('fake-mode')).toHaveCount(settings.live ? 0 : 1);

  // The live region refreshes itself; wait for one refresh so we test what the poller renders too.
  await expect(page.getByTestId('status-live')).toHaveAttribute('data-poll-at', /.+/, { timeout: 30_000 });

  for (const name of ['health', 'alarms', 'github', 'flags', 'cost']) {
    const panel = page.locator(`[data-panel=${name}]`);
    await expect(panel, `${name} panel`).toBeVisible();
    await expect(panel.getByTestId('panel-error'), `${name} panel error`).toHaveCount(0);
  }

  const health = page.locator('[data-panel=health]');
  for (const env of ['prod', 'staging']) {
    const card = health.locator(`[data-env=${env}]`);
    await expect(card, `${env} health`).toBeVisible();
    await expect(card).toHaveAttribute('data-health', 'ready');
    await expect(card.getByTestId('env-version')).not.toHaveText(/^(unknown)?$/);
  }

  const alarms = page.locator('[data-panel=alarms] [data-alarm]');
  await expect(alarms.first()).toBeVisible();
  for (const state of await alarms.evaluateAll(els => els.map(e => e.getAttribute('data-alarm-state')))) {
    expect(['OK', 'ALARM', 'INSUFFICIENT_DATA']).toContain(state);
  }

  const github = page.locator('[data-panel=github]');
  await expect(github.getByTestId('open-issues')).toHaveText(/^\d+$/);
  await expect(github.locator('[data-workflow]').first()).toBeVisible();

  const flags = page.locator('[data-panel=flags] [data-flag]');
  await expect(flags.first()).toBeVisible();
  const flagName = await flags.first().getAttribute('data-flag');
  await expect(page.getByTestId(`flag-toggle-${flagName}`)).toBeVisible();

  await expect(page.getByTestId('cost-mtd')).toHaveText(/\$\s?[\d,]+(\.\d+)?/);
  await expect(page.getByTestId('cost-today')).toHaveText(/\$\s?[\d,]+(\.\d+)?/);
});
