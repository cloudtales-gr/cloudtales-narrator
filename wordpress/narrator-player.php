<?php
/**
 * CloudTales Narrator: audio player at the top of every post.
 *
 * Install with the "Code Snippets" plugin (PHP snippet, run everywhere; omit the opening <?php tag)
 * or in a child theme's functions.php. Set CT_NARRATOR_AUDIO_BASE to the Bicep output 'audioEndpoint'.
 *
 * The player stays hidden until the audio's metadata loads, so posts without narration
 * (404) or requests from other sites (403) never show a broken player.
 */

const CT_NARRATOR_AUDIO_BASE = 'https://func-cloudtales-narrator-REPLACE.azurewebsites.net/api/audio/';

add_filter('the_content', function ($content) {
    if (!is_string($content) || !is_singular('post') || !in_the_loop() || !is_main_query()) {
        return $content;
    }

    $slug = get_post_field('post_name', get_the_ID());
    $src  = CT_NARRATOR_AUDIO_BASE . rawurlencode($slug);

    $player = sprintf(
        '<figure class="ct-listen" hidden>'
        . '<figcaption>Listen to this article</figcaption>'
        . '<audio controls preload="metadata" src="%s" '
        . 'onloadedmetadata="this.closest(\'.ct-listen\').hidden = false"></audio>'
        . '</figure>',
        esc_url($src)
    );

    return $player . $content;
}, 5);
