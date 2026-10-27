# Deploying RepoScout on Vercel

RepoScout deploys as one Vercel project with two services defined in the root `vercel.json`:
the Vite frontend (`src/RepoScout.Web`) and the .NET API (`src/RepoScout.Api`, built from its `Dockerfile`
and run with Vercel's Container runtime). Requests to `/api/*` go to the API, everything else to the frontend.

## Setup

1. In Vercel, import the GitHub repository as a new project.
2. Set **Root Directory** to the repo root (`vercel.json` lives there).
3. Add these environment variables for **Production** and **Preview**:
   - `AiModel__ApiKey` - the Gemini API key.
   - `PORT=8080` - Vercel routes to port 80 by default, but the API image listens on 8080
     (`ASPNETCORE_HTTP_PORTS`); port 80 fails under the non-root image.
4. Deploy.

## Workflow

- Push to `main` deploys to production.
- Every pull request gets a preview deployment.

## Optional local check

`vercel dev -L` runs the services locally (requires Docker).

## Notes

- The Container runtime and Services are in beta.
- The Hobby plan is for non-commercial use only and has a 300 s maximum request duration.
- The API instance scales to zero after 5 minutes idle, so the first API call after that is a cold start.
  The static frontend is not affected.
