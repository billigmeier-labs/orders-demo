# 7. Workflow automation acts as its own GitHub App

- Date: 2026-11-09
- Status: Accepted

## Decision

Workflow automation acts as its own GitHub App, `orders-automation[bot]`.

## Why

Labels, triage and incident comments, issues and merges were showing as Josh (AI review and preview comments come from `github-actions[bot]`); the PAT stays only for the AWS → GitHub dispatch and the console (which acts for the presenter).
