import type { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js'
import { z } from 'zod'
import { getPackage, searchPackages, UgcApiError, type PackageDetail } from '../data/ugc-client.js'

function formatPackageDetails(pkg: PackageDetail, ident: string): string {
  const sections: string[] = []

  sections.push(`# ${pkg.Title}`)
  sections.push(`**Identifier:** \`${ident}\``)
  sections.push(`**Type:** ${pkg.TypeName}`)
  if (pkg.Summary) sections.push(`**Summary:** ${pkg.Summary}`)
  sections.push(`**Author:** ${pkg.Org.Title} (${pkg.Org.Ident})`)

  sections.push('')
  sections.push('## Stats')
  if (pkg.VotesUp) sections.push(`- Votes: ${pkg.VotesUp}`)
  if (pkg.Favourited) sections.push(`- Favourited: ${pkg.Favourited}`)

  sections.push('')
  sections.push('## Dates')
  sections.push(`- Created: ${pkg.Created}`)
  sections.push(`- Updated: ${pkg.Updated}`)

  if (pkg.Tags?.length) {
    sections.push('')
    sections.push(`## Tags\n${pkg.Tags.join(', ')}`)
  }

  if (pkg.Thumb) {
    sections.push('')
    sections.push(`## Thumbnail\n${pkg.Thumb}`)
  }

  sections.push('')
  sections.push(`## Usage\nUse \`${ident}\` in your S&box project to reference this package.`)
  sections.push(`Workshop page: https://sbox.game/${pkg.Org.Ident}/${pkg.Ident}`)

  return sections.join('\n')
}

export function registerGetPackage(server: McpServer) {
  server.registerTool(
    'get_package',
    {
      title: 'Get S&box Package Details',
      description:
        'Get detailed information about a specific S&box Workshop package by its identifier (e.g., "facepunch.zombiemale"). Returns full metadata including stats, tags, author info, and usage instructions.',
      inputSchema: {
        ident: z
          .string()
          .describe("Package identifier in 'org.name' format (e.g., 'facepunch.zombiemale', 'fpopium.chair_01')"),
      },
    },
    async ({ ident }) => {
      try {
        const pkg = await getPackage(ident)

        return {
          content: [{ type: 'text' as const, text: formatPackageDetails(pkg, ident) }],
        }
      } catch (err) {
        if (!(err instanceof UgcApiError && err.status === 404)) {
          return {
            content: [{
              type: 'text' as const,
              text: `Error fetching package: ${err instanceof Error ? err.message : String(err)}`,
            }],
          }
        }

        // Not found - fall back to search for similar packages
        const parts = ident.split('.')
        const searchQuery = parts.length > 1 ? parts[1] : ident

        try {
          const response = await searchPackages({ query: searchQuery, take: 5 })
          const suggestions = response.Packages
            .map(p => `  - ${p.FullIdent} ("${p.Title}")`)
            .join('\n')

          return {
            content: [{
              type: 'text' as const,
              text: suggestions
                ? `Package "${ident}" not found.\n\nSimilar packages:\n${suggestions}`
                : `Package "${ident}" not found.`,
            }],
          }
        } catch {
          // The primary lookup already 404'd (that is why we are here); the
          // suggestion search then failed too. Report not-found, not the 404.
          return {
            content: [{
              type: 'text' as const,
              text: `Package "${ident}" not found. (Could not fetch similar-package suggestions.)`,
            }],
          }
        }
      }
    }
  )
}
