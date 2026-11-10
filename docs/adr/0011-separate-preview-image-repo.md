# 11. Preview images live in their own ECR repository

- Date: 2026-11-09
- Status: Accepted

## Decision

Preview images live in their own ECR repository; release tags are immutable.

## Why

A PR could otherwise overwrite the image production runs.
